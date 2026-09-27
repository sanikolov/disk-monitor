using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DiskMonitor.Infrastructure;
using DiskMonitor.Models;

namespace DiskMonitor.Events;

internal sealed class EventLogMonitor : IDisposable
{
    private const int ErrorEventQueryResultStale = 15011;
    private const int ErrorEventQueryResultInvalidPosition = 15012;

    private static readonly string[] Providers =
    {
        "disk", "Disk", "Microsoft-Windows-Disk",
        "storport", "Storport", "Microsoft-Windows-StorPort", "Microsoft-Windows-Storage-Storport",
        "stornvme", "Microsoft-Windows-StorNvme",
        "storahci", "Microsoft-Windows-StorAHCI",
        "UASPStor",
        "Microsoft-Windows-StorDiag",
        "Ntfs", "NTFS", "Microsoft-Windows-Ntfs",
        "Kernel-PnP", "Microsoft-Windows-Kernel-PnP",
        "USBSTOR", "Microsoft-Windows-USBSTOR",
        "Microsoft-Windows-DriverFrameworks-UserMode"
    };

    private static readonly string[] Channels =
    {
        "System",
        "Microsoft-Windows-Ntfs/Operational",
        "Microsoft-Windows-Storage-ClassPnP/Operational",
        "Microsoft-Windows-Storage-Storport/Operational"
    };

    private static readonly string[] RequiredChannels =
    {
        "System",
        "Microsoft-Windows-Ntfs/Operational",
        "Microsoft-Windows-Storage-ClassPnP/Operational"
    };

    private static readonly string[] NtfsProviders = { "Ntfs", "NTFS", "Microsoft-Windows-Ntfs" };

    private readonly MonitorLogger _logger;
    private readonly DateTimeOffset _bootTimeUtc;
    private readonly object _watcherGate = new();
    private readonly Dictionary<string, EventLogWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long RecordId, EventBookmark Bookmark)> _lastBookmarks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _startingChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _restartingChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _started;
    private bool _disposed;

    public EventLogMonitor(MonitorLogger logger, DateTimeOffset bootTimeUtc)
    {
        _logger = logger;
        _bootTimeUtc = bootTimeUtc;
    }

    public event EventHandler<StorageObservation>? ObservationReceived;

    public IReadOnlyList<string> GetMissingRequiredChannels()
    {
        lock (_watcherGate)
        {
            return RequiredChannels.Where(channel => !_activeChannels.Contains(channel)).ToArray();
        }
    }

    public void RetryMissingRequiredChannels()
    {
        foreach (var channel in GetMissingRequiredChannels())
        {
            // Resume from the last delivered bookmark, or replay this boot if none
            // exists. One blocked channel must not delay the other sources.
            _ = Task.Run(() => StartWatcher(channel, readExistingEvents: true));
        }
    }

    public void Start()
    {
        lock (_watcherGate)
        {
            if (_disposed || _started)
            {
                return;
            }

            _started = true;
        }

        foreach (var channel in Channels)
        {
            // An Event Log API call can block. Keep WinForms startup and the other
            // channels responsive while each subscription is established.
            _ = Task.Run(() => StartWatcher(channel, readExistingEvents: true));
        }

        _logger.Info("Windows storage event subscription attempts started.");
    }

    private void StartWatcher(string channel, bool readExistingEvents)
    {
        lock (_watcherGate)
        {
            if (_disposed || _watchers.ContainsKey(channel) || !_startingChannels.Add(channel))
            {
                return;
            }
        }

        try
        {
            _ = EventLogSession.GlobalSession.GetLogInformation(channel, PathType.LogName);
            using (var configuration = new EventLogConfiguration(channel))
            {
                if (!configuration.IsEnabled)
                {
                    _logger.Info($"The Windows '{channel}' event log is disabled; monitoring will continue without it.");
                    return;
                }
            }

            var query = new EventLogQuery(
                channel,
                PathType.LogName,
                BuildQuery(channel, _bootTimeUtc, DateTimeOffset.UtcNow));
            EventBookmark? bookmark;
            lock (_watcherGate)
            {
                bookmark = _lastBookmarks.TryGetValue(channel, out var last) ? last.Bookmark : null;
            }

            try
            {
                EnableWatcher(channel, query, bookmark, readExistingEvents);
            }
            catch (Exception ex) when (bookmark is not null && IsStaleBookmarkError(ex))
            {
                // Windows invalidates bookmarks when a channel is cleared or rolls
                // over. Resume from the retained part of this boot exactly once.
                lock (_watcherGate)
                {
                    if (_lastBookmarks.TryGetValue(channel, out var last) &&
                        ReferenceEquals(last.Bookmark, bookmark))
                    {
                        _lastBookmarks.Remove(channel);
                    }
                }

                _logger.Warn($"The Windows '{channel}' event-log bookmark is stale; replaying retained records since boot.", ex);
                EnableWatcher(channel, query, bookmark: null, readExistingEvents: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Unable to subscribe to the Windows '{channel}' event log.", ex);
        }
        finally
        {
            lock (_watcherGate)
            {
                _startingChannels.Remove(channel);
            }
        }
    }

    private void EnableWatcher(string channel, EventLogQuery query, EventBookmark? bookmark, bool readExistingEvents)
    {
        var watcher = new EventLogWatcher(query, bookmark, readExistingEvents);
        watcher.EventRecordWritten += OnEventRecordWritten;
        bool alreadyRegistered;
        lock (_watcherGate)
        {
            alreadyRegistered = _disposed || _watchers.ContainsKey(channel);
            if (!alreadyRegistered)
            {
                _watchers[channel] = watcher;
            }
        }

        if (alreadyRegistered)
        {
            DisposeWatcher(watcher);
            return;
        }

        try
        {
            watcher.Enabled = true;
            bool isActive;
            lock (_watcherGate)
            {
                isActive = !_disposed && !_restartingChannels.Contains(channel) &&
                           _watchers.TryGetValue(channel, out var current) && ReferenceEquals(current, watcher);
                if (isActive)
                {
                    _activeChannels.Add(channel);
                }
            }

            if (isActive)
            {
                _logger.Info($"Windows '{channel}' event-log watcher is active.");
            }
        }
        catch
        {
            RemoveWatcher(channel, watcher);
            throw;
        }
    }

    internal static bool IsStaleBookmarkError(Exception exception) =>
        exception is EventLogException &&
        (exception.HResult & 0xFFFF) is ErrorEventQueryResultStale or ErrorEventQueryResultInvalidPosition;

    private void OnEventRecordWritten(object? sender, EventRecordWrittenEventArgs args)
    {
        if (args.EventException is not null)
        {
            var channel = sender is EventLogWatcher failedWatcher
                ? GetWatcherChannel(failedWatcher)
                : null;
            if (channel is not null && IsStaleBookmarkError(args.EventException))
            {
                lock (_watcherGate)
                {
                    _lastBookmarks.Remove(channel);
                }

                _logger.Warn($"The Windows '{channel}' event-log bookmark became stale; recovery will replay retained records since boot.");
            }

            _logger.Warn(
                $"The Windows '{channel ?? "unknown"}' event-log watcher reported a subscription failure.",
                args.EventException);
            if (sender is EventLogWatcher watcher)
            {
                ScheduleRestart(watcher);
            }

            return;
        }

        if (args.EventRecord is null)
        {
            _logger.Warn("The Windows event-log watcher delivered no event record.");
            return;
        }

        try
        {
            var observation = EventObservationParser.Parse(args.EventRecord, _bootTimeUtc);
            if (observation is not null)
            {
                ObservationReceived?.Invoke(this, observation);
            }

            var channel = args.EventRecord.LogName ??
                          (sender is EventLogWatcher watcher ? GetWatcherChannel(watcher) : null);
            var recordId = args.EventRecord.RecordId;
            if (channel is not null && recordId is not null)
            {
                lock (_watcherGate)
                {
                    // Watchers can overlap during recovery. Keep the furthest point
                    // seen so replay starts after the newest processed record.
                    if (!_lastBookmarks.TryGetValue(channel, out var last) || recordId.Value > last.RecordId)
                    {
                        _lastBookmarks[channel] = (recordId.Value, args.EventRecord.Bookmark);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("A Windows event could not be normalized.", ex);
        }
        finally
        {
            args.EventRecord.Dispose();
        }
    }

    private string? GetWatcherChannel(EventLogWatcher watcher)
    {
        lock (_watcherGate)
        {
            return _watchers.FirstOrDefault(pair => ReferenceEquals(pair.Value, watcher)).Key;
        }
    }

    public void Dispose()
    {
        EventLogWatcher[] watchers;
        lock (_watcherGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            watchers = _watchers.Values.ToArray();
            _watchers.Clear();
            _activeChannels.Clear();
            _startingChannels.Clear();
            _restartingChannels.Clear();
        }

        foreach (var watcher in watchers)
        {
            DisposeWatcher(watcher);
        }

        _shutdown.Dispose();
    }

    private void ScheduleRestart(EventLogWatcher failedWatcher)
    {
        string? channel;
        lock (_watcherGate)
        {
            channel = _watchers.FirstOrDefault(pair => ReferenceEquals(pair.Value, failedWatcher)).Key;
            if (channel is null || _disposed || !_restartingChannels.Add(channel))
            {
                return;
            }

            _activeChannels.Remove(channel);
        }

        _ = RestartWatcherAsync(channel, failedWatcher);
    }

    private async Task RestartWatcherAsync(string channel, EventLogWatcher failedWatcher)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _shutdown.Token).ConfigureAwait(false);
            RemoveWatcher(channel, failedWatcher);
            lock (_watcherGate)
            {
                // The failed watcher is detached. Permit the replacement to become
                // active; StartWatcher still serializes concurrent recovery attempts.
                _restartingChannels.Remove(channel);
            }

            // Resume after the last delivered record so an outage is caught up
            // without replaying the entire boot on every recovery.
            StartWatcher(channel, readExistingEvents: true);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"The Windows '{channel}' event-log watcher could not be restarted.", ex);
        }
        finally
        {
            lock (_watcherGate)
            {
                _restartingChannels.Remove(channel);
            }
        }
    }

    private void RemoveWatcher(string channel, EventLogWatcher expected)
    {
        EventLogWatcher? watcher;
        lock (_watcherGate)
        {
            if (!_watchers.TryGetValue(channel, out watcher) || !ReferenceEquals(watcher, expected))
            {
                return;
            }

            _watchers.Remove(channel);
            _activeChannels.Remove(channel);
        }

        DisposeWatcher(watcher);
    }

    private void DisposeWatcher(EventLogWatcher watcher)
    {
        watcher.EventRecordWritten -= OnEventRecordWritten;
        try
        {
            watcher.Enabled = false;
        }
        catch (Exception ex)
        {
            _logger.Warn("The Windows event-log watcher could not be disabled cleanly.", ex);
        }

        try
        {
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warn("The Windows event-log watcher could not be disposed cleanly.", ex);
        }
    }

    internal static string BuildQuery(
        string channel,
        DateTimeOffset bootTimeUtc,
        DateTimeOffset queryTimeUtc)
    {
        // Include a small overlap so events recorded at the boot boundary are not lost
        // while the watcher is being created. The parser applies the exact cutoff.
        var uptime = queryTimeUtc - bootTimeUtc;
        var milliseconds = Math.Max(
            1,
            (long)Math.Ceiling(Math.Max(0, uptime.TotalMilliseconds) + TimeSpan.FromMinutes(1).TotalMilliseconds));
        var informationalNtfsFilter = string.Join(" or ", NtfsProviders.Select(provider => $"Provider[@Name='{provider}']"));
        var escapedChannel = System.Security.SecurityElement.Escape(channel);
        // Windows Event Log rejects the combined provider/level/time predicate once
        // it has too many XPath terms. Separate Select elements form a union while
        // keeping each predicate small enough for the event service to evaluate.
        var warningAndErrorSelects = Providers.Chunk(6).Select(group =>
        {
            var providers = string.Join(" or ", group.Select(provider => $"Provider[@Name='{provider}']"));
            return $"<Select Path=\"{escapedChannel}\">*[System[({providers}) and (Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) &lt;= {milliseconds}]]]</Select>";
        });

        return $"""
            <QueryList>
              <Query Id="0">
                {string.Join(Environment.NewLine, warningAndErrorSelects)}
                <Select Path="{escapedChannel}">*[System[Level=4 and EventID=98 and ({informationalNtfsFilter}) and TimeCreated[timediff(@SystemTime) &lt;= {milliseconds}]]]</Select>
              </Query>
            </QueryList>
            """;
    }
}

internal static class EventObservationParser
{
    private static readonly Regex DiskNumberRegex = new(@"(?:Disk\s*#?|PhysicalDrive|Harddisk)(?<number>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DriveLetterRegex = new(@"(?<![A-Za-z])(?<drive>[A-Za-z]):", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeviceTokenRegex = new(@"(?<token>\\Device\\[^,\s)]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RaidPortRegex = new(@"RaidPort(?<number>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] StorageCommandFields =
    {
        "DeviceNumber", "Vendor", "Model", "SerialNumber", "SrbStatus",
        "ScsiStatus", "SenseKey", "AdditionalSenseCode", "CdbBytes", "NumberOfRetriesDone"
    };

    public static StorageObservation? Parse(EventRecord record, DateTimeOffset bootTimeUtc)
    {
        var provider = record.ProviderName ?? string.Empty;
        var id = record.Id;
        var details = SafeFormatDescription(record, "Windows reported a storage-related event.");
        var xml = SafeXml(record);
        return ParseEvent(
            provider,
            id,
            record.Level.GetValueOrDefault(),
            record.RecordId,
            record.TimeCreated,
            details,
            xml,
            bootTimeUtc,
            record.LogName);
    }

    internal static StorageObservation? ParseEvent(
        string provider,
        int id,
        int level,
        long? eventRecordId,
        DateTime? timeCreated,
        string details,
        string xml,
        DateTimeOffset? historicalCutoff = null,
        string? channel = null)
    {
        var values = ExtractEventValues(xml);
        var payloadText = string.Join("\n", values.Values);
        var searchText = LimitSearchText($"{details}\n{payloadText}", 8000);
        var rule = GetRule(provider.ToLowerInvariant(), id, level, values, searchText);
        if (rule is null)
        {
            return null;
        }

        var timestamp = timeCreated.HasValue ? ToUtc(timeCreated.Value) : DateTimeOffset.UtcNow;
        var identityText = $"{payloadText}\n{details}";
        // ClassPnP reports a physical DeviceNumber but no volume. It must be correlated
        // with the inventory by serial as disk numbers can change after a restart.
        var diskNumber = rule.Id == "storage.command-failed"
            ? ParseClassPnpDeviceNumber(values)
            : ParseDiskNumber(values) ?? ParseDiskNumber(identityText);
        var deviceSerialNumber = values.TryGetValue("DeviceSerialNumber", out var ntfsSerial)
            ? ntfsSerial.Trim()
            : values.TryGetValue("SerialNumber", out var storageSerial)
                ? storageSerial.Trim()
                : null;
        var driveLetter = ParseDriveLetter(values) ??
                          (rule.Category == ObservationCategory.Ntfs ? ParseDriveLetter(details) : null);
        var deviceToken = ParseDeviceToken(values, identityText);
        var raidPort = ParseRaidPort(identityText);
        var observationDetails = rule.Id == "storage.command-failed"
            ? AddStorageCommandFields(details, values)
            : details;

        return new StorageObservation
        {
            Timestamp = timestamp,
            Source = provider,
            Channel = channel ?? string.Empty,
            EventId = id,
            EventRecordId = eventRecordId,
            RuleId = rule.Id,
            Category = rule.Category,
            Severity = rule.Severity,
            AlertMode = rule.AlertMode,
            Summary = rule.Summary,
            Details = observationDetails,
            DiskNumber = diskNumber,
            DeviceSerialNumber = deviceSerialNumber,
            RaidPortNumber = raidPort,
            DriveLetter = driveLetter,
            DeviceToken = deviceToken,
            IsHistorical = timestamp < (historicalCutoff ?? DateTimeOffset.UtcNow.AddMinutes(-2))
        };
    }

    private static EventRule? GetRule(
        string provider,
        int id,
        int level,
        IReadOnlyDictionary<string, string> values,
        string searchText)
    {
        if (provider is "disk" or "microsoft-windows-disk")
        {
            return id switch
            {
                7 => new EventRule("disk.bad-block", ObservationCategory.Storage, ObservationSeverity.Critical, AlertMode.Immediate, "Windows reported a bad block or device error."),
                11 => new EventRule("disk.controller-error", ObservationCategory.Storage, ObservationSeverity.Error, AlertMode.Immediate, "Windows reported a disk controller error."),
                15 => new EventRule("disk.device-not-ready", ObservationCategory.Storage, ObservationSeverity.Warning, AlertMode.Repeated, "Windows reported that the disk was not ready."),
                51 => new EventRule("disk.io-error", ObservationCategory.Storage, ObservationSeverity.Warning, AlertMode.Repeated, "Windows reported an I/O error while accessing the disk."),
                153 => new EventRule("disk.io-retry", ObservationCategory.Storage, ObservationSeverity.Warning, AlertMode.Repeated, "Windows retried an I/O operation on the disk."),
                154 => new EventRule("disk.hardware-error", ObservationCategory.Storage, ObservationSeverity.Critical, AlertMode.Immediate, "Windows reported that an I/O operation failed because of a hardware error."),
                157 => new EventRule("disk.surprise-removal", ObservationCategory.Usb, ObservationSeverity.Error, AlertMode.Immediate, "Windows reported that the disk was surprise-removed."),
                _ => null
            };
        }

        if (provider.Contains("storport", StringComparison.Ordinal) ||
            provider.Contains("storahci", StringComparison.Ordinal) ||
            provider.Contains("stornvme", StringComparison.Ordinal) ||
            provider.Contains("uaspstor", StringComparison.Ordinal))
        {
            return id switch
            {
                129 => new EventRule("storage.reset-timeout", ObservationCategory.Storage, ObservationSeverity.Error, AlertMode.Repeated, "Windows reset a storage device after a timeout."),
                140 => new EventRule("storage.device-missing", ObservationCategory.Storage, ObservationSeverity.Error, AlertMode.Repeated, "Windows reported that a storage device or path was missing."),
                _ => null
            };
        }

        if (provider.Contains("ntfs", StringComparison.Ordinal))
        {
            if (id == 98 && !IsActionableChkdskEvent(values, searchText))
            {
                return null;
            }

            return id switch
            {
                50 => new EventRule("ntfs.delayed-write", ObservationCategory.Ntfs, ObservationSeverity.Critical, AlertMode.Immediate, "NTFS reported a delayed-write or I/O failure; data may not have reached the device."),
                55 => new EventRule("ntfs.corruption", ObservationCategory.Ntfs, ObservationSeverity.Critical, AlertMode.Immediate, "NTFS reported that the filesystem structure is corrupt."),
                98 => new EventRule("ntfs.chkdsk-required", ObservationCategory.Ntfs, ObservationSeverity.Critical, AlertMode.Immediate, "NTFS reported that the volume requires a full filesystem check."),
                140 => new EventRule("ntfs.device-missing", ObservationCategory.Ntfs, ObservationSeverity.Error, AlertMode.Immediate, "NTFS reported that a device does not exist or is unavailable."),
                147 => new EventRule("ntfs.slow-io", ObservationCategory.Ntfs, ObservationSeverity.Warning, AlertMode.Repeated, "NTFS reported a slow I/O operation."),
                _ => null
            };
        }

        if (provider == "microsoft-windows-stordiag" && id == 507)
        {
            // A failed non-data SCSI command may be a rejected capability probe.
            // Repetition warrants investigation without implying lost user data.
            return new EventRule("storage.command-failed", ObservationCategory.Storage, ObservationSeverity.Warning, AlertMode.Repeated, "Windows reported a failed non-read/write storage command.");
        }

        if (provider.Contains("kernel-pnp", StringComparison.Ordinal))
        {
            return level <= 3 && IsStorageDeviceEvent(values)
                ? new EventRule(
                    "usb.device-error",
                    ObservationCategory.Usb,
                    level switch
                    {
                        1 => ObservationSeverity.Critical,
                        2 => ObservationSeverity.Error,
                        _ => ObservationSeverity.Warning
                    },
                    AlertMode.Repeated,
                    "Windows reported an error while configuring a USB storage device.")
                : null;
        }

        if (provider is "usbstor" or "microsoft-windows-usbstor")
        {
            return level <= 3
                ? new EventRule(
                    "usb.device-error",
                    ObservationCategory.Usb,
                    level switch
                    {
                        1 => ObservationSeverity.Critical,
                        2 => ObservationSeverity.Error,
                        _ => ObservationSeverity.Warning
                    },
                    AlertMode.Repeated,
                    "Windows reported an error while configuring a USB storage device.")
                : null;
        }

        if (provider.Contains("driverframeworks-usermode", StringComparison.Ordinal) && (id is 10110 or 10111))
        {
            return IsStorageDeviceEvent(values)
                ? new EventRule("usb.framework-error", ObservationCategory.Usb, ObservationSeverity.Error, AlertMode.Repeated, "Windows reported a USB storage device-framework failure.")
                : null;
        }

        return null;
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        var local = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Local) : value;
        return new DateTimeOffset(local).ToUniversalTime();
    }

    private static bool IsActionableChkdskEvent(IReadOnlyDictionary<string, string> values, string text)
    {
        if (values.TryGetValue("CorruptionActionState", out var stateText) &&
            uint.TryParse(
                stateText,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var state))
        {
            return state != 0;
        }

        return text.Contains("full chkdsk", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("needs to be taken offline", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("file system structure", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("corrupt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStorageDeviceEvent(IReadOnlyDictionary<string, string> values)
    {
        var deviceValues = values
            .Where(pair => pair.Key.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.Contains("instance", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.Equals("DriverName", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.Contains("classguid", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value);
        var deviceText = string.Join("\n", deviceValues);
        if (string.IsNullOrWhiteSpace(deviceText))
        {
            return false;
        }

        var allText = string.Join("\n", values.Values);
        var hasUsbStorageId = allText.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase) ||
                              allText.Contains("UASPSTOR", StringComparison.OrdinalIgnoreCase);
        var hasScsiDiskId = deviceText.Contains("SCSI\\DISK", StringComparison.OrdinalIgnoreCase);
        var hasUsbDiskClass = deviceText.Contains("USB\\VID_", StringComparison.OrdinalIgnoreCase) &&
                              deviceText.Contains("{4d36e967-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase);
        return hasUsbStorageId || hasScsiDiskId || hasUsbDiskClass;
    }

    private static string? ParseDeviceToken(IReadOnlyDictionary<string, string> values, string identityText)
    {
        var deviceValues = values
            .Where(pair => pair.Key.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.Contains("instance", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.Equals("DriverName", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        var pnpIdentifier = deviceValues.FirstOrDefault(value =>
                                value.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase) ||
                                value.Contains("UASPSTOR", StringComparison.OrdinalIgnoreCase) ||
                                value.Contains("SCSI\\DISK", StringComparison.OrdinalIgnoreCase)) ??
                            deviceValues.FirstOrDefault(value =>
                                value.Contains("USB\\VID_", StringComparison.OrdinalIgnoreCase));
        if (pnpIdentifier is not null)
        {
            return pnpIdentifier;
        }

        var match = DeviceTokenRegex.Match(identityText);
        return match.Success ? match.Groups["token"].Value : null;
    }

    private static string SafeFormatDescription(EventRecord record, string fallback)
    {
        try
        {
            return record.FormatDescription()?.Trim() is { Length: > 0 } description
                ? description
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string SafeXml(EventRecord record)
    {
        try
        {
            return record.ToXml();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Dictionary<string, string> ExtractEventValues(string xml)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return values;
        }

        try
        {
            var document = XDocument.Parse(xml);
            var ns = XNamespace.Get("http://schemas.microsoft.com/win/2004/08/events/event");
            foreach (var data in document.Descendants(ns + "Data"))
            {
                var name = data.Attribute("Name")?.Value ?? $"value{values.Count}";
                values[name] = data.Value;
            }

            foreach (var element in document
                         .Descendants()
                         .Where(element => element.Name.LocalName.Equals("UserData", StringComparison.OrdinalIgnoreCase))
                         .SelectMany(userData => userData.Descendants().Where(element => !element.Elements().Any())))
            {
                values[element.Name.LocalName] = element.Value;
            }
        }
        catch
        {
            // The formatted event text remains useful if an unusual provider emits malformed XML.
        }

        return values;
    }

    private static int? ParseDiskNumber(IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values.Where(pair => pair.Key.Contains("disknumber", StringComparison.OrdinalIgnoreCase) ||
                                                   pair.Key.Equals("disk", StringComparison.OrdinalIgnoreCase) ||
                                                   pair.Key.Contains("physicaldrive", StringComparison.OrdinalIgnoreCase)))
        {
            var number = Regex.Match(pair.Value, @"(?<!\d)\d+(?!\d)");
            if (number.Success && int.TryParse(number.Value, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? ParseClassPnpDeviceNumber(Dictionary<string, string> values) =>
        values.TryGetValue("DeviceNumber", out var text) && int.TryParse(text, out var number)
            ? number
            : null;

    private static string AddStorageCommandFields(string description, Dictionary<string, string> values)
    {
        // StorDiag 507's formatted message omits the fields needed to distinguish
        // a rejected capability probe from retries or failed user-data writes.
        var fields = StorageCommandFields
            .Where(values.ContainsKey)
            .Select(name => $"{name}={values[name].Trim()}");
        return $"{description} {string.Join("; ", fields)}".Trim();
    }

    private static int? ParseDiskNumber(string text)
    {
        var match = DiskNumberRegex.Match(text);
        return match.Success && int.TryParse(match.Groups["number"].Value, out var value) ? value : null;
    }

    private static string? ParseDriveLetter(IReadOnlyDictionary<string, string> values)
    {
        foreach (var value in values
                     .Where(pair => pair.Key.Contains("drive", StringComparison.OrdinalIgnoreCase) ||
                                    pair.Key.Contains("volume", StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Value))
        {
            var drive = ParseDriveLetter(value);
            if (drive is not null)
            {
                return drive;
            }
        }

        return null;
    }

    private static string? ParseDriveLetter(string text)
    {
        var match = DriveLetterRegex.Match(text);
        return match.Success ? $"{match.Groups["drive"].Value.ToUpperInvariant()}:" : null;
    }

    private static int? ParseRaidPort(string text)
    {
        var match = RaidPortRegex.Match(text);
        return match.Success && int.TryParse(match.Groups["number"].Value, out var value) ? value : null;
    }

    private static string LimitSearchText(string text, int maximum) =>
        text.Length <= maximum ? text : text[..maximum];

    private sealed record EventRule(
        string Id,
        ObservationCategory Category,
        ObservationSeverity Severity,
        AlertMode AlertMode,
        string Summary);
}
