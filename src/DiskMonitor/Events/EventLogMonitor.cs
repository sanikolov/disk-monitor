using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DiskMonitor.Infrastructure;
using DiskMonitor.Models;

namespace DiskMonitor.Events;

internal sealed class EventLogMonitor : IDisposable
{
    private static readonly string[] Providers =
    {
        "disk", "Disk", "Microsoft-Windows-Disk",
        "storport", "Storport", "Microsoft-Windows-StorPort", "Microsoft-Windows-Storage-Storport",
        "stornvme", "Microsoft-Windows-StorNvme",
        "storahci", "Microsoft-Windows-StorAHCI",
        "UASPStor",
        "Ntfs", "NTFS", "Microsoft-Windows-Ntfs",
        "Kernel-PnP", "Microsoft-Windows-Kernel-PnP",
        "USBSTOR", "Microsoft-Windows-USBSTOR",
        "Microsoft-Windows-DriverFrameworks-UserMode"
    };

    private static readonly string[] Channels =
    {
        "System",
        "Microsoft-Windows-Ntfs/Operational",
        "Microsoft-Windows-Storage-Storport/Operational"
    };

    private static readonly string[] NtfsProviders = { "Ntfs", "NTFS", "Microsoft-Windows-Ntfs" };

    private readonly MonitorLogger _logger;
    private readonly DateTimeOffset _bootTimeUtc;
    private readonly object _watcherGate = new();
    private readonly Dictionary<string, EventLogWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _restartingChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public EventLogMonitor(MonitorLogger logger, DateTimeOffset bootTimeUtc)
    {
        _logger = logger;
        _bootTimeUtc = bootTimeUtc;
    }

    public event EventHandler<StorageObservation>? ObservationReceived;

    public void Start()
    {
        lock (_watcherGate)
        {
            if (_disposed || _watchers.Count > 0)
            {
                return;
            }
        }

        foreach (var channel in Channels)
        {
            StartWatcher(channel, readExistingEvents: true);
        }

        lock (_watcherGate)
        {
            if (_watchers.Count > 0)
            {
                _logger.Info("Windows storage event monitoring started.");
            }
        }
    }

    private void StartWatcher(string channel, bool readExistingEvents)
    {
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
            var watcher = new EventLogWatcher(query, null, readExistingEvents);
            watcher.EventRecordWritten += OnEventRecordWritten;
            lock (_watcherGate)
            {
                if (_disposed)
                {
                    DisposeWatcher(watcher);
                    return;
                }

                _watchers[channel] = watcher;
            }

            try
            {
                watcher.Enabled = true;
            }
            catch
            {
                RemoveWatcher(channel, watcher);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Unable to subscribe to the Windows '{channel}' event log.", ex);
        }
    }

    private void OnEventRecordWritten(object? sender, EventRecordWrittenEventArgs args)
    {
        if (args.EventException is not null)
        {
            var channel = sender is EventLogWatcher failedWatcher
                ? GetWatcherChannel(failedWatcher)
                : null;
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
        }

        _ = RestartWatcherAsync(channel, failedWatcher);
    }

    private async Task RestartWatcherAsync(string channel, EventLogWatcher failedWatcher)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _shutdown.Token).ConfigureAwait(false);
            RemoveWatcher(channel, failedWatcher);
            StartWatcher(channel, readExistingEvents: false);
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
        var providerFilter = string.Join(" or ", Providers.Select(provider => $"Provider[@Name='{provider}']"));
        var informationalNtfsFilter = string.Join(" or ", NtfsProviders.Select(provider => $"Provider[@Name='{provider}']"));
        var escapedChannel = System.Security.SecurityElement.Escape(channel);

        return $"""
            <QueryList>
              <Query Id="0">
                <Select Path="{escapedChannel}">*[System[
                  ({providerFilter}) and
                  ((Level=1 or Level=2 or Level=3) or
                   (Level=4 and EventID=98 and ({informationalNtfsFilter}))) and
                  TimeCreated[timediff(@SystemTime) &lt;= {milliseconds}]
                ]]</Select>
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
        var diskNumber = ParseDiskNumber(values) ?? ParseDiskNumber(identityText);
        var driveLetter = ParseDriveLetter(values) ??
                          (rule.Category == ObservationCategory.Ntfs ? ParseDriveLetter(details) : null);
        var deviceToken = ParseDeviceToken(values, identityText);
        var raidPort = ParseRaidPort(identityText);

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
            Details = details,
            DiskNumber = diskNumber,
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
                _ => null
            };
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
