using System.Threading.Channels;
using DiskMonitor.Events;
using DiskMonitor.Infrastructure;
using DiskMonitor.Models;
using DiskMonitor.Storage;

namespace DiskMonitor.Monitoring;

internal sealed class DiskMonitorCoordinator : IDisposable
{
    private readonly MonitorLogger _logger;
    private readonly MonitorOptions _options;
    private readonly DateTimeOffset _bootTimeUtc;
    private readonly StorageInventory _inventoryReader;
    private readonly EventLogMonitor _eventLogMonitor;
    private readonly DeviceChangeMonitor _deviceChangeMonitor;
    private readonly IncidentEngine _incidentEngine;
    private readonly Channel<StorageObservation> _observations = Channel.CreateUnbounded<StorageObservation>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<bool> _inventoryReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _inventoryGate = new();
    private readonly object _deviceDebounceGate = new();
    private readonly Dictionary<string, bool> _lastPredictionByDevice = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _predictionStatusByDevice = new(StringComparer.Ordinal);
    private readonly Dictionary<int, LastKnownDevice> _lastKnownDevicesByNumber = new();
    private readonly Dictionary<string, UsbFlapState> _usbFlapStates = new(StringComparer.Ordinal);
    private StorageInventorySnapshot _currentInventory = new();
    private CancellationTokenSource? _deviceDebounce;
    private DateTimeOffset _lastRefreshStartedAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public DiskMonitorCoordinator(
        MonitorLogger logger,
        JsonStateStore stateStore,
        MonitorOptions options,
        DateTimeOffset? bootTimeUtc = null)
    {
        _logger = logger;
        _options = options;
        _bootTimeUtc = bootTimeUtc ?? SystemBootTime.GetUtc();
        _inventoryReader = new StorageInventory(logger);
        _eventLogMonitor = new EventLogMonitor(logger, _bootTimeUtc);
        _deviceChangeMonitor = new DeviceChangeMonitor();
        _incidentEngine = new IncidentEngine(stateStore, logger, options);

        _eventLogMonitor.ObservationReceived += OnObservationReceived;
        _deviceChangeMonitor.DeviceChanged += OnDeviceChanged;
        _deviceChangeMonitor.ShutdownRequested += OnShutdownRequested;
        _incidentEngine.AlertRaised += OnAlertRaised;
        _incidentEngine.IncidentsChanged += OnIncidentsChanged;
    }

    public event EventHandler<IncidentRaisedEventArgs>? AlertRaised;
    public event EventHandler? IncidentsChanged;
    public event EventHandler? ShutdownRequested;

    public DateTimeOffset BootTimeUtc => _bootTimeUtc;

    public IReadOnlyList<Incident> GetIncidents(bool activeOnly = false) => _incidentEngine.GetIncidents(activeOnly);

    public IReadOnlyList<Incident> GetCurrentBootIncidents(bool activeOnly = false) =>
        _incidentEngine.GetIncidents(activeOnly)
            .Where(incident => incident.LastSeen >= _bootTimeUtc)
            .ToList();

    public IReadOnlyList<DeviceSnapshot> GetDevices()
    {
        lock (_inventoryGate)
        {
            return _currentInventory.Devices.ToList();
        }
    }

    public string GetPredictionStatus(string deviceKey)
    {
        lock (_inventoryGate)
        {
            return _predictionStatusByDevice.TryGetValue(deviceKey, out var status)
                ? status
                : "Not checked";
        }
    }

    public void Start()
    {
        _incidentEngine.ResolveBefore(_bootTimeUtc);
        _deviceChangeMonitor.Start();
        _eventLogMonitor.Start();
        _ = Task.Run(ProcessObservationsAsync);
        _ = Task.Run(RefreshLoopAsync);
    }

    public void CheckNow() => RequestRefresh(bypassMinimum: true);

    public void Acknowledge(string incidentId) => _incidentEngine.Acknowledge(incidentId);

    public void Resolve(string incidentId) => _incidentEngine.Resolve(incidentId);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _observations.Writer.TryComplete();
        _eventLogMonitor.Dispose();
        _deviceChangeMonitor.Dispose();
        lock (_deviceDebounceGate)
        {
            _deviceDebounce?.Cancel();
            _deviceDebounce?.Dispose();
            _deviceDebounce = null;
        }

        _incidentEngine.Dispose();
    }

    private void OnObservationReceived(object? sender, StorageObservation observation)
    {
        if (observation.IsHistorical)
        {
            return;
        }

        _observations.Writer.TryWrite(observation);
    }

    private void OnDeviceChanged(object? sender, DeviceChangeEventArgs args)
    {
        _logger.Info(args.IsRemoval ? "A device removal was reported by Windows." : "A device topology change was reported by Windows.");
        lock (_deviceDebounceGate)
        {
            _deviceDebounce?.Cancel();
            _deviceDebounce?.Dispose();
            _deviceDebounce = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _ = DebouncedRefreshAsync(_deviceDebounce.Token);
        }
    }

    private void OnShutdownRequested(object? sender, EventArgs args) => ShutdownRequested?.Invoke(this, EventArgs.Empty);

    private void OnAlertRaised(object? sender, IncidentRaisedEventArgs args) => AlertRaised?.Invoke(this, args);

    private void OnIncidentsChanged(object? sender, EventArgs args) => IncidentsChanged?.Invoke(this, args);

    private void RequestRefresh(bool bypassMinimum = false)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _refreshSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A refresh is already pending.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown is in progress.
        }
    }

    private async Task ProcessObservationsAsync()
    {
        try
        {
            await _inventoryReady.Task.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            await foreach (var observation in _observations.Reader.ReadAllAsync(_shutdown.Token))
            {
                DeviceSnapshot? device;
                lock (_inventoryGate)
                {
                    device = FindDeviceForObservationLocked(observation);
                }

                _incidentEngine.Process(observation, device);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("The observation processing loop stopped unexpectedly.", ex);
        }
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            var initialRefresh = InitialRefreshAsync();
            var initialTimeout = Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(_options.InitialInventoryTimeoutSeconds, 5, 120)),
                _shutdown.Token);
            await Task.WhenAny(initialRefresh, initialTimeout).ConfigureAwait(false);
            _inventoryReady.TrySetResult(true);
            if (!initialRefresh.IsCompleted)
            {
                _logger.Warn("Initial storage inventory is taking longer than expected; event monitoring will continue with best-effort device attribution.");
            }

            await initialRefresh.ConfigureAwait(false);

            while (!_shutdown.IsCancellationRequested)
            {
                _ = await _refreshSignal.WaitAsync(
                    TimeSpan.FromMinutes(Math.Clamp(_options.PollIntervalMinutes, 5, 1440)),
                    _shutdown.Token).ConfigureAwait(false);
                if (_shutdown.IsCancellationRequested)
                {
                    return;
                }

                await RefreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("The storage refresh loop stopped unexpectedly.", ex);
        }
    }

    private async Task InitialRefreshAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("The initial storage inventory refresh failed; event monitoring will continue.", ex);
        }
    }

    private async Task RefreshAsync()
    {
        lock (_inventoryGate)
        {
            _lastRefreshStartedAt = DateTimeOffset.UtcNow;
        }

        StorageInventorySnapshot snapshot;
        try
        {
            snapshot = await Task.Run(_inventoryReader.Read, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }

        if (!snapshot.IsReliable)
        {
            _logger.Warn("Windows returned no reliable storage inventory; retaining the previous snapshot and skipping health/flapping decisions.");
            return;
        }

        StorageInventorySnapshot previous;
        lock (_inventoryGate)
        {
            previous = _currentInventory;
            _currentInventory = snapshot;
            foreach (var device in snapshot.Devices)
            {
                if (device.DiskNumber >= 0)
                {
                    _lastKnownDevicesByNumber[device.DiskNumber] = new LastKnownDevice(device, DateTimeOffset.UtcNow);
                }
            }
        }

        ObserveUsbFlapping(previous, snapshot, DateTimeOffset.UtcNow);
        foreach (var device in snapshot.Devices)
        {
            _incidentEngine.ReidentifyDevice(device);
            var priorDevice = previous.FindByDiskNumber(device.DiskNumber);
            EnqueueHealthObservations(device, priorDevice);
        }

        await ProbeFailurePredictionAsync(snapshot).ConfigureAwait(false);
        _incidentEngine.AutoResolveStale(DateTimeOffset.UtcNow);
    }

    private void EnqueueHealthObservations(DeviceSnapshot device, DeviceSnapshot? previous)
    {
        var isNewIdentity = previous is null || !previous.Key.Equals(device.Key, StringComparison.Ordinal);
        if (device.HealthStatus is 1 or 2 && (isNewIdentity || previous!.HealthStatus != device.HealthStatus))
        {
            _observations.Writer.TryWrite(new StorageObservation
            {
                Timestamp = DateTimeOffset.UtcNow,
                RuleId = "health.status",
                Category = ObservationCategory.Health,
                Severity = device.HealthStatus == 2 ? ObservationSeverity.Critical : ObservationSeverity.Warning,
                AlertMode = AlertMode.Immediate,
                Summary = device.HealthStatus == 2 ? "Windows reports the disk as unhealthy." : "Windows reports a disk health warning.",
                Details = $"Windows health status: {device.HealthStatus}; operational status: {string.Join(", ", device.OperationalStatus)}.",
                DiskNumber = device.DiskNumber
            });
        }

        var abnormal = HasAbnormalOperationalStatus(device);
        var wasAbnormal = previous is not null && HasAbnormalOperationalStatus(previous);
        if (abnormal && (isNewIdentity || !wasAbnormal || !string.Join("|", previous!.OperationalStatus).Equals(string.Join("|", device.OperationalStatus), StringComparison.OrdinalIgnoreCase)))
        {
            _observations.Writer.TryWrite(new StorageObservation
            {
                Timestamp = DateTimeOffset.UtcNow,
                RuleId = "health.operational-status",
                Category = ObservationCategory.Health,
                Severity = ObservationSeverity.Error,
                AlertMode = AlertMode.Immediate,
                Summary = "Windows reports an abnormal disk operational status.",
                Details = $"Operational status: {string.Join(", ", device.OperationalStatus)}.",
                DiskNumber = device.DiskNumber
            });
        }
    }

    private async Task ProbeFailurePredictionAsync(StorageInventorySnapshot snapshot)
    {
        foreach (var device in snapshot.Devices)
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (device.IsUsb)
                {
                    SetPredictionStatus(device, "Skipped (USB bridge; telemetry only)");
                    continue;
                }

                if (!NativeStorage.TryPredictFailure(device.DiskNumber, out var predictsFailure, out var errorCode))
                {
                    SetPredictionStatus(device, PredictionFailureStatus(errorCode));
                    continue;
                }

                bool previousPrediction;
                lock (_inventoryGate)
                {
                    previousPrediction = _lastPredictionByDevice.TryGetValue(device.Key, out var previousValue) && previousValue;
                    _lastPredictionByDevice[device.Key] = predictsFailure;
                }
                SetPredictionStatus(device, predictsFailure
                    ? "Supported (failure predicted)"
                    : "Supported (no failure predicted)");

                if (predictsFailure && !previousPrediction)
                {
                    _observations.Writer.TryWrite(new StorageObservation
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        RuleId = "health.predictive-failure",
                        Category = ObservationCategory.Health,
                        Severity = ObservationSeverity.Critical,
                        AlertMode = AlertMode.Immediate,
                        Summary = "The storage device predicts a failure.",
                        Details = "Windows' storage failure-prediction request reported a predicted device failure.",
                        DiskNumber = device.DiskNumber
                    });
                }
            }
            catch (Exception ex)
            {
                SetPredictionStatus(device, $"Unavailable (query exception: {ex.GetType().Name})");
            }

            await Task.Yield();
        }
    }

    internal static bool HasAbnormalOperationalStatus(DeviceSnapshot device) =>
        device.OperationalStatus.Any(status => status.Contains("Predictive", StringComparison.OrdinalIgnoreCase) ||
                                               status.Contains("Pred Fail", StringComparison.OrdinalIgnoreCase) ||
                                               status.Contains("degraded", StringComparison.OrdinalIgnoreCase) ||
                                               status.Contains("stressed", StringComparison.OrdinalIgnoreCase) ||
                                               status.Contains("nonrecover", StringComparison.OrdinalIgnoreCase) ||
                                               status.Contains("not ready", StringComparison.OrdinalIgnoreCase) ||
                                               status.Contains("offline", StringComparison.OrdinalIgnoreCase) ||
                                               status.Contains("no contact", StringComparison.OrdinalIgnoreCase) ||
                                               status.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                               status.Contains("lost", StringComparison.OrdinalIgnoreCase) ||
                                               status.Contains("failed", StringComparison.OrdinalIgnoreCase));

    private async Task DebouncedRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            DateTimeOffset lastRefresh;
            lock (_inventoryGate)
            {
                lastRefresh = _lastRefreshStartedAt;
            }

            var minimumInterval = TimeSpan.FromSeconds(Math.Clamp(_options.MinimumTopologyRefreshSeconds, 5, 300));
            var remaining = minimumInterval - (DateTimeOffset.UtcNow - lastRefresh);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }

            RequestRefresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private DeviceSnapshot? FindDeviceForObservationLocked(StorageObservation observation)
    {
        DeviceSnapshot? device = null;
        if (observation.DiskNumber is not null)
        {
            device = _currentInventory.FindByDiskNumber(observation.DiskNumber.Value);
        }

        if (device is null && observation.DriveLetter is not null)
        {
            device = _currentInventory.FindByDriveLetter(observation.DriveLetter);
        }

        if (device is null && observation.RaidPortNumber is not null)
        {
            device = _currentInventory.FindByRaidPort(observation.RaidPortNumber.Value);
        }

        if (device is not null || observation.DiskNumber is null)
        {
            return device;
        }

        if (_lastKnownDevicesByNumber.TryGetValue(observation.DiskNumber.Value, out var lastKnown) &&
            DateTimeOffset.UtcNow - lastKnown.SeenAt <= TimeSpan.FromMinutes(Math.Clamp(_options.DeviceNameGraceMinutes, 1, 240)))
        {
            return lastKnown.Device;
        }

        return null;
    }

    private void ObserveUsbFlapping(StorageInventorySnapshot previous, StorageInventorySnapshot current, DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Clamp(_options.UsbFlapWindowMinutes, 1, 1440));
        var threshold = Math.Clamp(_options.UsbFlapThreshold, 2, 20);
        var currentKeys = current.Devices
            .Where(device => device.IsUsb)
            .Select(device => device.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var device in previous.Devices.Where(device => device.IsUsb && !currentKeys.Contains(device.Key)))
        {
            if (!_usbFlapStates.TryGetValue(device.Key, out var state))
            {
                state = new UsbFlapState();
                _usbFlapStates[device.Key] = state;
            }

            state.LastKnownDevice = device;
            state.IsMissing = true;
            state.MissingSince = now;
        }

        foreach (var device in current.Devices.Where(device => device.IsUsb))
        {
            if (!_usbFlapStates.TryGetValue(device.Key, out var state))
            {
                state = new UsbFlapState();
                _usbFlapStates[device.Key] = state;
            }

            if (state.IsMissing)
            {
                state.IsMissing = false;
                state.MissingSince = null;
                state.FlapTimes.Enqueue(now);
            }

            while (state.FlapTimes.Count > 0 && now - state.FlapTimes.Peek() > window)
            {
                state.FlapTimes.Dequeue();
            }

            if (state.FlapTimes.Count >= threshold &&
                (state.LastAlertedAt is null || now - state.LastAlertedAt.Value > window))
            {
                _observations.Writer.TryWrite(new StorageObservation
                {
                    Timestamp = now,
                    RuleId = "usb.flapping",
                    Category = ObservationCategory.Usb,
                    Severity = ObservationSeverity.Error,
                    AlertMode = AlertMode.Immediate,
                    Summary = "Windows observed repeated USB disconnects and reconnects.",
                    Details = $"{device.DisplayName} disappeared and reappeared at least {state.FlapTimes.Count} times in {window.TotalMinutes:0} minutes.",
                    DiskNumber = device.DiskNumber,
                    DriveLetter = device.DriveLetters.Count > 0 ? device.DriveLetters[0] : null
                });
                state.LastAlertedAt = now;
            }

            state.LastKnownDevice = device;
        }

        var staleKeys = _usbFlapStates
            .Where(pair => pair.Value.IsMissing && pair.Value.MissingSince is not null &&
                           now - pair.Value.MissingSince.Value > window * 2)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in staleKeys)
        {
            _usbFlapStates.Remove(key);
        }
    }

    private void SetPredictionStatus(DeviceSnapshot device, string status)
    {
        var changed = false;
        lock (_inventoryGate)
        {
            if (!_predictionStatusByDevice.TryGetValue(device.Key, out var previous) ||
                !previous.Equals(status, StringComparison.Ordinal))
            {
                _predictionStatusByDevice[device.Key] = status;
                changed = true;
            }
        }

        if (changed)
        {
            _logger.Info($"Failure prediction for {device.DisplayName}: {status}.");
            IncidentsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string PredictionFailureStatus(int errorCode) => errorCode switch
    {
        5 => "Unavailable (access denied)",
        1 or 2 or 6 or 50 or 87 => "Unavailable (not supported)",
        _ => $"Unavailable (Win32 error {errorCode})"
    };

    private sealed class LastKnownDevice
    {
        public LastKnownDevice(DeviceSnapshot device, DateTimeOffset seenAt)
        {
            Device = device;
            SeenAt = seenAt;
        }

        public DeviceSnapshot Device { get; }
        public DateTimeOffset SeenAt { get; }
    }

    private sealed class UsbFlapState
    {
        public DeviceSnapshot? LastKnownDevice { get; set; }
        public Queue<DateTimeOffset> FlapTimes { get; } = new();
        public DateTimeOffset? LastAlertedAt { get; set; }
        public DateTimeOffset? MissingSince { get; set; }
        public bool IsMissing { get; set; }
    }
}
