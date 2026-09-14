using DiskMonitor.Infrastructure;
using DiskMonitor.Models;

namespace DiskMonitor.Monitoring;

internal sealed class IncidentRaisedEventArgs : EventArgs
{
    public IncidentRaisedEventArgs(Incident incident)
    {
        Incident = incident;
    }

    public Incident Incident { get; }
}

internal sealed class IncidentEngine : IDisposable
{
    private const int MaximumProcessedKeys = 5000;
    private const int MaximumRetainedIncidents = 500;
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly IMonitorStateStore _stateStore;
    private readonly MonitorLogger _logger;
    private readonly MonitorOptions _options;
    private readonly MonitorState _state;
    private readonly HashSet<string> _processedEvidenceKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _processedEvidenceOrder = new();
    private readonly System.Threading.Timer _saveTimer;
    private MonitorState? _pendingSave;
    private bool _disposed;

    public IncidentEngine(IMonitorStateStore stateStore, MonitorLogger logger, MonitorOptions options)
    {
        _stateStore = stateStore;
        _logger = logger;
        _options = options;
        _state = _stateStore.Load();
        _state.Incidents ??= new List<Incident>();
        _state.ProcessedEvidenceKeys ??= new List<string>();

        foreach (var key in _state.ProcessedEvidenceKeys)
        {
            if (_processedEvidenceKeys.Add(key))
            {
                _processedEvidenceOrder.Enqueue(key);
            }
        }

        TrimProcessedKeysLocked();
        _saveTimer = new System.Threading.Timer(_ => FlushPendingSave(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler<IncidentRaisedEventArgs>? AlertRaised;
    public event EventHandler? IncidentsChanged;

    public IReadOnlyList<Incident> GetIncidents(bool activeOnly = false)
    {
        lock (_gate)
        {
            return (activeOnly
                    ? _state.Incidents.Where(incident => !incident.Resolved)
                    : _state.Incidents)
                .Select(CloneIncident)
                .ToList();
        }
    }

    public void Process(StorageObservation observation, DeviceSnapshot? device)
    {
        Incident? alert = null;
        var changed = false;

        lock (_gate)
        {
            if (_disposed || !_processedEvidenceKeys.Add(observation.EvidenceKey))
            {
                return;
            }

            _processedEvidenceOrder.Enqueue(observation.EvidenceKey);
            TrimProcessedKeysLocked();

            var deviceKey = device?.Key ?? BuildFallbackDeviceKey(observation);
            var fingerprint = $"{deviceKey}|{observation.RuleId}";
            var incident = _state.Incidents.FirstOrDefault(item => !item.Resolved && item.Fingerprint == fingerprint);
            var previousSeverity = incident?.Severity;

            if (incident is null)
            {
                incident = new Incident
                {
                    Fingerprint = fingerprint,
                    DeviceKey = deviceKey,
                    DeviceName = device?.DisplayName ?? BuildFallbackDeviceName(observation),
                    Category = observation.Category,
                    Severity = observation.Severity,
                    FirstSeen = observation.Timestamp,
                    LastSeen = observation.Timestamp
                };
                _state.Incidents.Insert(0, incident);
            }
            else
            {
                incident.FirstSeen = observation.Timestamp < incident.FirstSeen ? observation.Timestamp : incident.FirstSeen;
                incident.LastSeen = observation.Timestamp > incident.LastSeen ? observation.Timestamp : incident.LastSeen;
                incident.Severity = (ObservationSeverity)Math.Max((int)incident.Severity, (int)observation.Severity);
                if (device is not null)
                {
                    incident.DeviceName = device.DisplayName;
                }
            }

            incident.EvidenceCount++;
            incident.Evidence.Add(new IncidentEvidence
            {
                Timestamp = observation.Timestamp,
                Summary = observation.Summary,
                Details = observation.Details,
                EventId = observation.EventId,
                EventRecordId = observation.EventRecordId
            });
            if (incident.Evidence.Count > 20)
            {
                incident.Evidence.RemoveRange(0, incident.Evidence.Count - 20);
            }

            var recentCutoff = incident.LastSeen.AddMinutes(-_options.RepeatedEventWindowMinutes);
            var recentCount = incident.Evidence.Count(item => item.Timestamp >= recentCutoff && item.Timestamp <= incident.LastSeen);
            var thresholdReached = observation.Severity == ObservationSeverity.Critical ||
                                   observation.AlertMode == AlertMode.Immediate ||
                                   (observation.AlertMode == AlertMode.Repeated && recentCount >= _options.RepeatedEventThreshold);
            var cooldownElapsed = incident.LastAlerted is null ||
                                  incident.LastSeen - incident.LastAlerted.Value >= TimeSpan.FromMinutes(_options.AlertCooldownMinutes);
            var severityEscalated = previousSeverity is not null && observation.Severity > previousSeverity;

            if (!observation.IsHistorical && thresholdReached && (cooldownElapsed || severityEscalated))
            {
                incident.LastAlerted = incident.LastSeen;
                incident.Acknowledged = false;
                alert = CloneIncident(incident);
            }

            PruneIncidentsLocked();
            ScheduleSaveLocked();
            changed = true;
        }

        if (alert is not null)
        {
            _logger.Warn($"Alert raised: incident={alert.Id}; device={alert.DeviceName}; key={alert.DeviceKey}; category={alert.Category}; rule={observation.RuleId}; observed={observation.Timestamp:O}; {observation.Details}");
            AlertRaised?.Invoke(this, new IncidentRaisedEventArgs(alert));
        }

        if (changed)
        {
            IncidentsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Acknowledge(string incidentId)
    {
        UpdateIncident(incidentId, incident => incident.Acknowledged = true);
    }

    public void Resolve(string incidentId)
    {
        UpdateIncident(incidentId, incident =>
        {
            incident.Acknowledged = true;
            incident.Resolved = true;
        });
    }

    public void ResolveBefore(DateTimeOffset cutoff)
    {
        var changed = false;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var incident in _state.Incidents.Where(incident => !incident.Resolved && incident.LastSeen < cutoff))
            {
                incident.Acknowledged = true;
                incident.Resolved = true;
                changed = true;
            }

            if (changed)
            {
                PruneIncidentsLocked();
                ScheduleSaveLocked();
            }
        }

        if (changed)
        {
            IncidentsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void AutoResolveStale(DateTimeOffset now)
    {
        var changed = false;
        var cutoff = now - TimeSpan.FromHours(Math.Clamp(_options.AutoResolveAfterHours, 1, 24 * 30));

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var incident in _state.Incidents.Where(incident => !incident.Resolved && incident.LastSeen < cutoff))
            {
                incident.Acknowledged = true;
                incident.Resolved = true;
                changed = true;
            }

            if (changed)
            {
                PruneIncidentsLocked();
                ScheduleSaveLocked();
            }
        }

        if (changed)
        {
            IncidentsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ReidentifyDevice(DeviceSnapshot device)
    {
        var changed = false;
        var fallbackKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            $"disk:{device.DiskNumber}"
        };
        if (device.ScsiPort is not null)
        {
            fallbackKeys.Add($"raidport:{device.ScsiPort.Value}");
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var fallback in _state.Incidents
                         .Where(incident => !incident.Resolved && fallbackKeys.Contains(incident.DeviceKey))
                         .ToList())
            {
                var separator = fallback.Fingerprint.LastIndexOf('|');
                if (separator < 0)
                {
                    continue;
                }

                var fingerprint = $"{device.Key}|{fallback.Fingerprint[(separator + 1)..]}";
                var existing = _state.Incidents.FirstOrDefault(incident =>
                    !incident.Resolved && incident.Fingerprint.Equals(fingerprint, StringComparison.Ordinal));
                if (existing is null || ReferenceEquals(existing, fallback))
                {
                    fallback.DeviceKey = device.Key;
                    fallback.Fingerprint = fingerprint;
                    fallback.DeviceName = device.DisplayName;
                    changed = true;
                    continue;
                }

                existing.FirstSeen = existing.FirstSeen < fallback.FirstSeen ? existing.FirstSeen : fallback.FirstSeen;
                existing.LastSeen = existing.LastSeen > fallback.LastSeen ? existing.LastSeen : fallback.LastSeen;
                existing.Severity = (ObservationSeverity)Math.Max((int)existing.Severity, (int)fallback.Severity);
                existing.EvidenceCount += fallback.EvidenceCount;
                existing.Evidence.AddRange(fallback.Evidence);
                existing.Evidence = existing.Evidence
                    .OrderBy(evidence => evidence.Timestamp)
                    .TakeLast(20)
                    .ToList();
                existing.Acknowledged &= fallback.Acknowledged;
                if (fallback.LastAlerted is not null &&
                    (existing.LastAlerted is null || fallback.LastAlerted > existing.LastAlerted))
                {
                    existing.LastAlerted = fallback.LastAlerted;
                }
                _state.Incidents.Remove(fallback);
                changed = true;
            }

            if (changed)
            {
                ScheduleSaveLocked();
            }
        }

        if (changed)
        {
            IncidentsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        MonitorState finalState;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            finalState = CloneStateLocked();
        }

        _saveTimer.Dispose();
        lock (_saveGate)
        {
            _pendingSave = null;
        }

        _stateStore.Save(finalState);
    }

    private void UpdateIncident(string incidentId, Action<Incident> update)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var incident = _state.Incidents.FirstOrDefault(item => item.Id.Equals(incidentId, StringComparison.OrdinalIgnoreCase));
            if (incident is null)
            {
                return;
            }

            update(incident);
            ScheduleSaveLocked();
        }

        IncidentsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ScheduleSaveLocked()
    {
        var snapshot = CloneStateLocked();
        lock (_saveGate)
        {
            _pendingSave = snapshot;
            if (!_disposed)
            {
                _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void FlushPendingSave()
    {
        lock (_saveGate)
        {
            var snapshot = _pendingSave;
            _pendingSave = null;
            if (snapshot is not null)
            {
                // Keep timer writes serialized with the final synchronous save in Dispose.
                // This remains outside _gate, so event processing is never blocked by disk I/O.
                _stateStore.Save(snapshot);
            }
        }
    }

    private MonitorState CloneStateLocked() => new()
    {
        Incidents = _state.Incidents.Select(CloneIncident).ToList(),
        ProcessedEvidenceKeys = _processedEvidenceOrder.ToList()
    };

    private static Incident CloneIncident(Incident source) => new()
    {
        Id = source.Id,
        Fingerprint = source.Fingerprint,
        DeviceKey = source.DeviceKey,
        DeviceName = source.DeviceName,
        Category = source.Category,
        Severity = source.Severity,
        FirstSeen = source.FirstSeen,
        LastSeen = source.LastSeen,
        EvidenceCount = source.EvidenceCount,
        Acknowledged = source.Acknowledged,
        Resolved = source.Resolved,
        LastAlerted = source.LastAlerted,
        Evidence = source.Evidence.Select(evidence => new IncidentEvidence
        {
            Timestamp = evidence.Timestamp,
            Summary = evidence.Summary,
            Details = evidence.Details,
            EventId = evidence.EventId,
            EventRecordId = evidence.EventRecordId
        }).ToList()
    };

    private void PruneIncidentsLocked()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-90);
        _state.Incidents.RemoveAll(incident => incident.Resolved && incident.LastSeen < cutoff);
        if (_state.Incidents.Count <= MaximumRetainedIncidents)
        {
            return;
        }

        var removable = _state.Incidents
            .Where(incident => incident.Resolved)
            .OrderBy(incident => incident.LastSeen)
            .ToList();
        foreach (var incident in removable)
        {
            if (_state.Incidents.Count <= MaximumRetainedIncidents)
            {
                break;
            }

            _state.Incidents.Remove(incident);
        }
    }

    private void TrimProcessedKeysLocked()
    {
        while (_processedEvidenceOrder.Count > MaximumProcessedKeys)
        {
            var key = _processedEvidenceOrder.Dequeue();
            _processedEvidenceKeys.Remove(key);
        }
    }

    private static string BuildFallbackDeviceKey(StorageObservation observation) =>
        observation.DiskNumber is not null
            ? $"disk:{observation.DiskNumber.Value}"
            : observation.DriveLetter is not null
                ? $"volume:{observation.DriveLetter.ToLowerInvariant()}"
                : observation.RaidPortNumber is not null
                    ? $"raidport:{observation.RaidPortNumber.Value}"
                    : observation.DeviceToken is not null
                        ? $"device:{observation.DeviceToken.ToLowerInvariant()}"
                        : "unknown";

    private static string BuildFallbackDeviceName(StorageObservation observation)
    {
        if (observation.DiskNumber is not null)
        {
            return $"PhysicalDrive{observation.DiskNumber.Value}";
        }

        if (observation.RaidPortNumber is not null)
        {
            return $"Storage port {observation.RaidPortNumber.Value}";
        }

        if (observation.DeviceToken is not null)
        {
            return $"Storage device {observation.DeviceToken}";
        }

        return observation.DriveLetter is null ? "Unknown storage device" : $"Volume {observation.DriveLetter}";
    }
}
