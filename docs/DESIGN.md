# Disk Monitor design

## Purpose

Disk Monitor is a small, standard-user Windows tray application that makes storage trouble visible to users who would not normally inspect Event Viewer. It correlates existing Windows storage telemetry with a lightweight periodic inventory query and shows an actionable alert when a problem is detected.

The first release intentionally does not attempt to be a SMART suite, filesystem repair tool, surface scanner, or ETW performance profiler.

## Runtime model

The application starts at interactive logon, creates one tray icon, and keeps the main window hidden. A background coordinator owns three activities:

1. Event-log watchers read the System, NTFS Operational, and Storage ClassPnP Operational channels plus Storport Operational when accessible. Broken subscriptions are retried after a short backoff and replay events since boot so records written during a subscription gap are recovered. A monitoring heartbeat retries missing required channels.
2. A periodic refresh queries current `Win32_PnPEntity` problem codes and `MSFT_Disk` / `Win32_DiskDrive` in separate timeboxed child processes, merging Windows health, operational status, bus, volume, PNP, SCSI-port, and PDO metadata. A hung disk enumeration cannot discard an already completed PnP check.
3. A lightweight `IOCTL_STORAGE_PREDICT_FAILURE` request is attempted in a separate timeboxed child process for each non-USB physical disk. Unsupported devices and USB bridges are reported as unavailable, not unhealthy; a hung prediction does not make a successful inventory stale.

The probe client drains bounded output, terminates timed-out children, and confirms their exit before allowing a replacement. A child that cannot be reaped blocks further probes of that type and is reported as degraded; it is never duplicated. Event observations continue with serial or volume attribution while the inventory is unavailable. The status window and tray update independently of the inventory loop, showing the last attempted and successful scan times, stalled or failed scans, missing event subscriptions, failed PnP checks, and diagnostic-log failures. A periodic heartbeat writes a diagnostic entry so a stopped logger becomes visible as stale coverage.

The event watchers start independently so a WMI failure cannot disable telemetry. Observation processing waits briefly for the first inventory when possible, preserving device attribution without making event monitoring depend on WMI. Plug-and-Play notifications request a debounced refresh with a minimum interval so a burst or flap does not cause repeated controller queries.

## Alert policy

Events are normalized into observations with a timestamp, provider, event ID, rule, severity, and best-effort disk/volume/RaidPort identity. At startup, the event-log query covers the full period since the current Windows boot. Entries before that boot are ignored, and active incidents left over from an earlier boot are resolved and excluded from the current assessment. Repeated rules alert only after the configured threshold; immediate rules alert on the first occurrence. Health observations are emitted on a transition, not on every periodic poll.

The status window assigns active incidents one of three user-facing risk levels. Benign means no active issue is recorded. Survivable covers warnings and recoverable errors such as retries, disconnects, and controller timeouts. Critical covers Windows reports associated with corruption, bad blocks, unhealthy media, hardware I/O failure, or predicted failure; it warns that data loss may be imminent or may already have occurred. Acknowledging an incident does not reduce its risk classification, and resolved incidents retain their historical severity. The status and alert windows follow Windows' application light/dark theme and refresh when Windows broadcasts a theme-setting change.

Critical observations bypass the repeated-event threshold, and an escalation to a higher severity bypasses the normal alert cooldown. A live alert restores a topmost alert window and also raises a notification-area balloon so a serious issue cannot remain only as a changed tray icon.

Incident evidence is deduplicated by event record ID where available, and by a timestamp/rule/device fallback otherwise. Incident state is cloned for UI consumers, retained in bounded history, and saved with a short debounce to avoid writing JSON for every individual event. Users can acknowledge an alert or resolve it from either the alert window or the status window. Current-boot incidents do not disappear merely because they are older than 24 hours; the next reboot closes prior-boot incidents.

Storport reset events usually identify only `\\Device\\RaidPortN`. When Windows exposes `Win32_DiskDrive.SCSIPort`, that number is used to correlate the event with an `MSFT_Disk` snapshot; otherwise the incident is retained as an unattributed storage-port issue rather than guessed onto a disk.

USB inventory diffs track repeated disappearance/reappearance of the same stable device identity. Three flaps in ten minutes produce a dedicated USB flapping incident. The lightweight prediction IOCTL is skipped for USB devices because bridges vary and probing can wake sleeping external disks; its supported/unsupported/access-denied result is shown once per non-USB device in the status window and log.

Kernel-PnP and UMDF rules require storage-shaped instance metadata (`USBSTOR\\`, `SCSI\\Disk`, or a USB VID with the Windows disk class GUID). NTFS event 98 is kept only when its text indicates an actionable offline/full-check/corruption condition.

NTFS Event 147 is a repeated warning for slow I/O on any disk bus. Storage ClassPnP / StorDiag Event 507 is a repeated warning for failed non-read/write SCSI commands; it does not by itself establish a failed data write or a disk timeout. Both use the normal two-events-within-15-minutes threshold. When an event includes a serial, attribution checks it before using an ephemeral disk number, so replayed boot events cannot be assigned to a different drive after renumbering.

Related StorDiag events 500/502/505 (failed reads) and 501/503/506 (failed writes) are immediate Error observations, kept separate from non-read/write probes. They establish a failed request without asserting permanent data loss. Events 504 (IOCTL), 508 (non-SCSI), 509 (PnP), 510 (power), and 523 (capacity) are repeated warnings. All ClassPnP observations use its physical DeviceNumber and serial and retain every EventData field, including unknown future fields. The decoder handles common CDB opcodes, decimal/hex SRB status flags, SCSI status, sense key/ASC/ASCQ, and common NTSTATUS values; missing or invalid fields remain unavailable rather than acquiring a default cause. Sense interpretation is qualified when autosense validity is absent. Definitions follow the [Windows SDK SCSI header](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/scsi.h) and [Microsoft's SRB status documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/srb/ns-srb-_storage_request_block).

Every normalized Windows event is written as one escaped JSON object in the separate local rolling `storage-events.log`, independently of alert thresholds, cooldowns, and persistent incident deduplication. A per-run 4,096-record cache bounds replay deduplication. The entry includes event/receipt/log timestamps, channel/record identity, publisher PID/TID/activity, raw XML (up to 64 KiB, explicitly marked when truncated), decoded details, inventory sample time/coverage, and the matched device snapshot. A snapshot can postdate a replayed event and is labeled accordingly; publisher execution context is never represented as the originating application. Incident evidence also stores event identity and latest known device context, and the scrollable alert displays its complete details. Each log remains bounded to a 2 MiB file plus one archive; capture it promptly during investigation. Separating events from `monitor.log` keeps boot replay from rotating out probe timing and heartbeat evidence. Logging-health checks expose write failures in either file; raw-event logging has no heartbeat requirement because it may legitimately be idle.

The parent logs hardware-probe starts, confirmed exits, failures/timeouts, worker PIDs, UTC start times, durations, and exit codes. The inventory worker logs the start/end of each existing WMI query and legacy disk snapshots, including endpoints omitted by MSFT_Disk. These entries enable timing correlation with inventory/PnP/prediction activity. Collecting this telemetry adds no hardware probes, raw SCSI requests, ETW session, elevation, or change to the USB prediction exclusion.

The separate PnP inventory catches present USB, disk, and storage-controller failures even when no disk was enumerated and no matching event was logged. This includes code 43 / device descriptor failures present before application startup. Disconnected remembered devices and unrelated non-USB device classes are excluded. Unknown USB endpoints are not assumed to be disks and are never probed through physical-drive IOCTLs. Each observation includes the name, instance ID, class, problem code, and timestamp. Multiple failed endpoints remain separate incidents even when sampled simultaneously.

Current PnP failures are observed on every refresh, with existing incident cooldowns limiting popups. This restores an alert at the next refresh if it was manually cleared while the fault remained. Absence alone does not prove recovery: device alerts remain available for investigation until cleared or the next reboot. A failed PnP query raises a monitoring-coverage warning, which is resolved after a confirmed successful query. The status window's Refresh button requests a hardware refresh, and incident details are visible there as well as in alerts and diagnostic logs.

## Privileges and packaging

The application manifest is `asInvoker`; it does not request UAC elevation. The MSI is per-machine and therefore normally requires elevation during installation. The installer is built with WixSharp and WiX 4, installs the self-contained x64 application, creates a Start Menu shortcut, and registers startup under `HKLM`.

Windows controls notification-area placement. The application supplies a colored disk icon and users can pin it in the taskbar notification-area settings, but the program cannot force placement beside Safely Remove Hardware.

## Deliberate limitations

- No raw ETW tracing. Slow I/O is visible only when Windows emits NTFS Event 147; arbitrary I/O latency below that provider's reporting threshold is not measured.
- No automatic `chkdsk`, SMART self-test, surface scan, repair, or data modification.
- SMART passthrough is not attempted; USB enclosure support therefore depends on Windows telemetry and the bridge's exposed capabilities.
- Device identity and controller correlation are best effort because Windows event payloads vary by driver and hardware.
