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
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly DateTimeOffset _bootTimeUtc;
    private readonly HardwareProbeClient _pnpProbe = new();
    private readonly HardwareProbeClient _inventoryProbe = new();
    private readonly HardwareProbeClient _predictionProbe = new();
    private readonly EventLogMonitor _eventLogMonitor;
    private readonly DeviceChangeMonitor _deviceChangeMonitor;
    private readonly IncidentEngine _incidentEngine;
    private readonly RefreshAttemptGate _refreshGate = new();
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
    private readonly object _predictionGate = new();
    private readonly Dictionary<string, bool> _lastPredictionByDevice = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _predictionStatusByDevice = new(StringComparer.Ordinal);
    private readonly Dictionary<int, LastKnownDevice> _lastKnownDevicesByNumber = new();
    private readonly Dictionary<string, UsbFlapState> _usbFlapStates = new(StringComparer.Ordinal);
    private StorageInventorySnapshot _currentInventory = new();
    private CancellationTokenSource? _deviceDebounce;
    private DateTimeOffset _lastRefreshStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastRefreshSucceededAt;
    private string? _inventoryIssue;
    private string? _pnpIssue;
    private bool _refreshInProgress;
    private (StorageInventorySnapshot Snapshot, long Generation)? _pendingPrediction;
    private long _predictionGeneration;
    private bool _predictionLoopRunning;
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
    public event EventHandler? MonitoringChanged;
    public event EventHandler<ShutdownRequestedEventArgs>? ShutdownRequested;

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

    public MonitorStatusSnapshot GetMonitoringStatus()
    {
        DateTimeOffset? lastAttempt;
        DateTimeOffset? lastSuccess;
        string? inventoryIssue;
        string? pnpIssue;
        bool inProgress;
        lock (_inventoryGate)
        {
            lastAttempt = _lastRefreshStartedAt == DateTimeOffset.MinValue ? null : _lastRefreshStartedAt;
            lastSuccess = _lastRefreshSucceededAt;
            inventoryIssue = _inventoryIssue;
            pnpIssue = _pnpIssue;
            inProgress = _refreshInProgress;
        }

        var log = _logger.GetHealth();
        return new MonitorStatusSnapshot
        {
            StartedUtc = _startedUtc,
            LastInventoryAttemptUtc = lastAttempt,
            LastInventorySuccessUtc = lastSuccess,
            InventoryInProgress = inProgress,
            InventoryIssue = inventoryIssue,
            PnpIssue = pnpIssue,
            InventoryTimeout = TimeSpan.FromSeconds(Math.Clamp(_options.InventoryRefreshTimeoutSeconds, 15, 300)),
            MaximumInventoryAge = TimeSpan.FromMinutes(Math.Clamp(_options.PollIntervalMinutes, 5, 1440)) +
                                  TimeSpan.FromSeconds(Math.Clamp(_options.InventoryRefreshTimeoutSeconds, 15, 300)) +
                                  TimeSpan.FromMinutes(1),
            MissingEventChannels = _eventLogMonitor.GetMissingRequiredChannels(),
            EventChannelStartupGrace = TimeSpan.FromSeconds(30),
            LastDiagnosticWriteUtc = log.LastSuccessfulWriteUtc,
            MaximumDiagnosticLogAge = TimeSpan.FromMinutes(Math.Clamp(_options.MonitoringHeartbeatMinutes, 1, 60) + 1),
            DiagnosticLogError = log.LastWriteError,
            DiagnosticLogPath = _logger.LogFile
        };
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
        _ = Task.Run(MonitoringHeartbeatAsync);
        _ = Task.Run(RecoverEventWatchersAsync);
    }

    public void CheckNow() => RequestRefresh();

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
        _pnpProbe.Dispose();
        _inventoryProbe.Dispose();
        _predictionProbe.Dispose();
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

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs args) =>
        ShutdownRequested?.Invoke(this, args);

    private void OnAlertRaised(object? sender, IncidentRaisedEventArgs args) => AlertRaised?.Invoke(this, args);

    private void OnIncidentsChanged(object? sender, EventArgs args) => NotifyIncidentsChanged();

    private void RequestRefresh()
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
                try
                {
                    DeviceSnapshot? device;
                    lock (_inventoryGate)
                    {
                        device = FindDeviceForObservationLocked(observation);
                    }

                    _incidentEngine.Process(observation, device);
                }
                catch (Exception ex)
                {
                    // One malformed event or persistence failure must not stop all later alerts.
                    _logger.Error($"Storage observation processing failed: source={observation.Source}; channel={observation.Channel}; event={observation.EventId}; record={observation.EventRecordId}; rule={observation.RuleId}.", ex);
                }
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
        var firstRefresh = true;
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                if (firstRefresh)
                {
                    await WaitForRefreshAttemptAsync(
                        TimeSpan.FromSeconds(Math.Clamp(_options.InitialInventoryTimeoutSeconds, 5, 120)))
                        .ConfigureAwait(false);
                    // Events can use serial/volume attribution even if WMI is stuck.
                    _inventoryReady.TrySetResult(true);
                    firstRefresh = false;
                    continue;
                }

                _ = await _refreshSignal.WaitAsync(
                    TimeSpan.FromMinutes(Math.Clamp(_options.PollIntervalMinutes, 5, 1440)),
                    _shutdown.Token).ConfigureAwait(false);
                if (_shutdown.IsCancellationRequested)
                {
                    return;
                }

                await WaitForRefreshAttemptAsync(
                    TimeSpan.FromSeconds(Math.Clamp(_options.InventoryRefreshTimeoutSeconds, 15, 300)))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _inventoryReady.TrySetResult(true);
                MarkInventoryIssue($"Storage refresh loop failed: {ex.GetType().Name}: {ex.Message}");
                _logger.Error("The storage refresh loop failed; it will retry.", ex);
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Clamp(_options.MinimumTopologyRefreshSeconds, 5, 300)),
                        _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task WaitForRefreshAttemptAsync(TimeSpan timeout)
    {
        try
        {
            // The child is given the full hardware timeout. The extra interval
            // lets the parent confirm termination before declaring its own wait
            // stalled; a timed-out worker must never overlap its replacement.
            var outcome = await _refreshGate.RunAsync(
                () => RefreshAsync(timeout), timeout + TimeSpan.FromSeconds(10), _shutdown.Token).ConfigureAwait(false);
            switch (outcome)
            {
                case RefreshAttemptOutcome.TimedOut:
                    MarkInventoryIssue($"Storage inventory exceeded its {timeout.TotalSeconds:0}-second wait; the current probe is still running.");
                    break;
                case RefreshAttemptOutcome.StillRunning:
                    MarkInventoryIssue($"The previous storage inventory is still running after {timeout.TotalSeconds:0} seconds.");
                    break;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MarkInventoryIssue($"Storage inventory attempt failed: {ex.GetType().Name}: {ex.Message}");
            _logger.Error("Storage inventory attempt failed; later refreshes will continue.", ex);
        }
    }

    private async Task MonitoringHeartbeatAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromMinutes(Math.Clamp(_options.MonitoringHeartbeatMinutes, 1, 60)),
                    _shutdown.Token).ConfigureAwait(false);
                try
                {
                    var status = GetMonitoringStatus();
                    _logger.Info($"Monitoring heartbeat: process={Environment.ProcessId}; boot={_bootTimeUtc:O}; inventoryAttempt={status.LastInventoryAttemptUtc:O}; inventorySuccess={status.LastInventorySuccessUtc:O}; inventoryInProgress={status.InventoryInProgress}; inventoryIssue={status.InventoryIssue ?? "none"}; pnpIssue={status.PnpIssue ?? "none"}; missingEventChannels={string.Join(",", status.MissingEventChannels)}; logPath={status.DiagnosticLogPath}.");
                    NotifyMonitoringChanged();
                }
                catch (Exception ex)
                {
                    _logger.Error("The monitoring heartbeat failed; it will retry.", ex);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task RecoverEventWatchersAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromMinutes(Math.Clamp(_options.MonitoringHeartbeatMinutes, 1, 60)),
                    _shutdown.Token).ConfigureAwait(false);
                try
                {
                    // Subscription setup may block in Windows. Keep it off the
                    // heartbeat path so stale coverage is still reported.
                    _eventLogMonitor.RetryMissingRequiredChannels();
                    NotifyMonitoringChanged();
                }
                catch (Exception ex)
                {
                    _logger.Error("Event-log subscription recovery failed; it will retry.", ex);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void MarkInventoryIssue(string issue)
    {
        DateTimeOffset lastAttempt;
        lock (_inventoryGate)
        {
            _inventoryIssue = issue;
            lastAttempt = _lastRefreshStartedAt;
        }

        _logger.Warn($"Storage monitoring degraded: process={Environment.ProcessId}; issue={issue}; lastAttempt={lastAttempt:O}.");
        NotifyMonitoringChanged();
    }

    private void NotifyMonitoringChanged()
    {
        try
        {
            MonitoringChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A UI subscriber must not stop the monitoring or heartbeat loops.
            _logger.Error("A monitoring-status subscriber failed.", ex);
        }
    }

    private void NotifyIncidentsChanged()
    {
        try
        {
            IncidentsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.Error($"Incident-status subscriber failed: process={Environment.ProcessId}.", ex);
        }
    }

    private async Task RefreshAsync(TimeSpan hardwareTimeout)
    {
        lock (_inventoryGate)
        {
            _lastRefreshStartedAt = DateTimeOffset.UtcNow;
            _refreshInProgress = true;
        }

        NotifyMonitoringChanged();

        StorageInventorySnapshot snapshot;
        try
        {
            // PnP and disk enumeration use separate children. A hung disk provider
            // cannot discard already retrieved PnP faults, and neither call can
            // hold the tray process indefinitely.
            var pnpTask = _pnpProbe.RunPnpAsync(
                TimeSpan.FromSeconds(Math.Clamp(_options.PnpRefreshTimeoutSeconds, 5, 60)),
                _shutdown.Token);
            var inventoryTask = _inventoryProbe.RunInventoryAsync(hardwareTimeout, _shutdown.Token);
            await ObservePnpProblemsAsync(pnpTask).ConfigureAwait(false);
            var probe = await inventoryTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(probe.WorkerLogError))
            {
                _logger.Warn($"Hardware inventory worker could not write its diagnostic log: process={Environment.ProcessId}; error={probe.WorkerLogError}.");
            }
            snapshot = probe.Inventory;

            if (!snapshot.IsReliable)
            {
                MarkInventoryIssue("Windows returned no reliable storage inventory; the previous device snapshot was retained.");
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
                var serialIsUnique = ReferenceEquals(snapshot.FindUniqueBySerialNumber(device.SerialNumber), device);
                _incidentEngine.ReidentifyDevice(device, serialIsUnique);
                var priorDevice = previous.FindByDiskNumber(device.DiskNumber);
                EnqueueHealthObservations(device, priorDevice);
            }

            lock (_inventoryGate)
            {
                _lastRefreshSucceededAt = DateTimeOffset.UtcNow;
                _inventoryIssue = null;
            }

            NotifyIncidentsChanged();
            // Prediction is independent of inventory freshness. A newer scan
            // supersedes old prediction work, but the single worker loop still
            // confirms termination of its current child before replacing it.
            SchedulePrediction(snapshot);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MarkInventoryIssue($"Storage inventory failed: {ex.GetType().Name}: {ex.Message}");
            _logger.Error("Storage inventory refresh failed; later refreshes will continue.", ex);
        }
        finally
        {
            lock (_inventoryGate)
            {
                _refreshInProgress = false;
            }

            NotifyMonitoringChanged();
        }
    }

    private async Task ObservePnpProblemsAsync(Task<HardwarePnpProbeResult> pnpTask)
    {
        try
        {
            var probe = await pnpTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(probe.WorkerLogError))
            {
                _logger.Warn($"PnP worker could not write its diagnostic log: process={Environment.ProcessId}; error={probe.WorkerLogError}.");
            }

            var coverageFailure = probe.PnpProblems.FirstOrDefault(problem =>
                problem.RuleId == PnpProblemInventory.UnavailableRule);
            SetPnpIssue(coverageFailure?.Details);
            if (coverageFailure is null)
            {
                // A confirmed healthy PnP query closes only the monitor-coverage
                // incident. It does not resolve any actual device problem code.
                var fingerprint = $"device:{PnpProblemInventory.UnavailableDeviceToken}|{PnpProblemInventory.UnavailableRule}";
                foreach (var incident in _incidentEngine.GetIncidents(activeOnly: true)
                             .Where(incident => incident.Fingerprint == fingerprint))
                {
                    _incidentEngine.Resolve(incident.Id);
                }
            }
            foreach (var problem in probe.PnpProblems)
            {
                try
                {
                    _incidentEngine.Process(problem, null);
                }
                catch (Exception ex)
                {
                    _logger.Error($"PnP observation processing failed: process={Environment.ProcessId}; rule={problem.RuleId}; device={problem.DeviceToken}.", ex);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var issue = $"PnP problem scan failed: {ex.GetType().Name}: {ex.Message}";
            SetPnpIssue(issue);
            _logger.Warn($"PnP problem scan failed: process={Environment.ProcessId}; coverage=USB/disk/controller problem codes.", ex);
            try
            {
                _incidentEngine.Process(new StorageObservation
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Source = "HardwareProbeWorker",
                    RuleId = PnpProblemInventory.UnavailableRule,
                    DeviceToken = PnpProblemInventory.UnavailableDeviceToken,
                    Category = ObservationCategory.Health,
                    Severity = ObservationSeverity.Warning,
                    AlertMode = AlertMode.Immediate,
                    Summary = "USB and disk device problem checks are unavailable.",
                    Details = issue
                }, null);
            }
            catch (Exception observationError)
            {
                _logger.Error($"PnP coverage warning could not be recorded: process={Environment.ProcessId}.", observationError);
            }
        }
    }

    private void SetPnpIssue(string? issue)
    {
        lock (_inventoryGate)
        {
            _pnpIssue = issue;
        }

        NotifyMonitoringChanged();
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

    private void SchedulePrediction(StorageInventorySnapshot snapshot)
    {
        bool startLoop;
        lock (_predictionGate)
        {
            _pendingPrediction = (snapshot, ++_predictionGeneration);
            startLoop = !_predictionLoopRunning;
            _predictionLoopRunning = true;
        }

        try
        {
            foreach (var device in snapshot.Devices)
            {
                SetPredictionStatus(device, device.IsUsb
                    ? "Skipped (USB bridge; telemetry only)"
                    : "Pending (current inventory not yet checked)", notify: false);
            }
            NotifyIncidentsChanged();
        }
        catch (Exception ex)
        {
            _logger.Error($"Prediction status could not be queued: process={Environment.ProcessId}.", ex);
        }

        if (startLoop)
        {
            _ = RunPendingPredictionsAsync();
        }
    }

    private async Task RunPendingPredictionsAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            (StorageInventorySnapshot Snapshot, long Generation) next;
            lock (_predictionGate)
            {
                if (_pendingPrediction is not { } pending)
                {
                    _predictionLoopRunning = false;
                    return;
                }

                next = pending;
                _pendingPrediction = null;
            }

            await ProbeFailurePredictionAsync(next.Snapshot, next.Generation).ConfigureAwait(false);
        }

        lock (_predictionGate)
        {
            _predictionLoopRunning = false;
        }
    }

    private bool IsCurrentPrediction(long generation)
    {
        lock (_predictionGate)
        {
            return generation == _predictionGeneration;
        }
    }

    private async Task ProbeFailurePredictionAsync(StorageInventorySnapshot snapshot, long generation)
    {
        try
        {
            foreach (var device in snapshot.Devices)
            {
                if (_shutdown.IsCancellationRequested || !IsCurrentPrediction(generation))
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

                    if (device.DiskNumber < 0)
                    {
                        SetPredictionStatus(device, "Unavailable (disk number unknown)");
                        continue;
                    }

                    var result = await _predictionProbe.RunPredictionAsync(
                        device.DiskNumber,
                        TimeSpan.FromSeconds(Math.Clamp(_options.PredictionTimeoutSeconds, 5, 60)),
                        _shutdown.Token).ConfigureAwait(false);
                    if (!IsCurrentPrediction(generation))
                    {
                        return;
                    }
                    if (!result.Supported)
                    {
                        SetPredictionStatus(device, PredictionFailureStatus(result.ErrorCode));
                        continue;
                    }

                    bool previousPrediction;
                    lock (_inventoryGate)
                    {
                        previousPrediction = _lastPredictionByDevice.TryGetValue(device.Key, out var previousValue) && previousValue;
                        _lastPredictionByDevice[device.Key] = result.PredictsFailure;
                    }
                    SetPredictionStatus(device, result.PredictsFailure
                        ? "Supported (failure predicted)"
                        : "Supported (no failure predicted)");

                    if (result.PredictsFailure && !previousPrediction)
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
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (!IsCurrentPrediction(generation))
                    {
                        return;
                    }

                    SetPredictionStatus(device, $"Unavailable ({ex.GetType().Name})");
                    _logger.Warn($"Failure-prediction probe failed: process={Environment.ProcessId}; disk={device.DiskNumber}; device={device.DisplayName}; key={device.Key}.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            // This task is intentionally independent of inventory freshness; it
            // must observe its own failure instead of becoming an unobserved task.
            _logger.Error($"Failure-prediction cycle failed: process={Environment.ProcessId}.", ex);
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
        var serial = observation.DeviceSerialNumber;
        if (!string.IsNullOrWhiteSpace(serial))
        {
            // Disk numbers are ephemeral, especially for USB devices. A unique serial
            // from the event is stronger evidence than the current disk number.
            var match = _currentInventory.FindUniqueBySerialNumber(serial);
            if (match is not null)
            {
                return match;
            }
        }

        bool CompatibleSerial(DeviceSnapshot candidate) =>
            string.IsNullOrWhiteSpace(serial) ||
            string.IsNullOrWhiteSpace(candidate.SerialNumber) ||
            candidate.SerialNumber.Equals(serial, StringComparison.OrdinalIgnoreCase);

        DeviceSnapshot? device = null;
        if (observation.DiskNumber is not null)
        {
            device = _currentInventory.FindByDiskNumber(observation.DiskNumber.Value);
            if (device is not null && !CompatibleSerial(device))
            {
                device = null;
            }
        }

        if (device is null && observation.DriveLetter is not null)
        {
            device = _currentInventory.FindByDriveLetter(observation.DriveLetter);
            if (device is not null && !CompatibleSerial(device))
            {
                device = null;
            }
        }

        if (device is null && observation.RaidPortNumber is not null)
        {
            device = _currentInventory.FindByRaidPort(observation.RaidPortNumber.Value);
            if (device is not null && !CompatibleSerial(device))
            {
                device = null;
            }
        }

        if (device is not null || observation.DiskNumber is null)
        {
            return device;
        }

        if (_lastKnownDevicesByNumber.TryGetValue(observation.DiskNumber.Value, out var lastKnown) &&
            CompatibleSerial(lastKnown.Device) &&
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

    private void SetPredictionStatus(DeviceSnapshot device, string status, bool notify = true)
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
            if (notify)
            {
                NotifyIncidentsChanged();
            }
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
