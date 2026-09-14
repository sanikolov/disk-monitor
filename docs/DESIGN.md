# Disk Monitor design

## Purpose

Disk Monitor is a small, standard-user Windows tray application that makes storage trouble visible to users who would not normally inspect Event Viewer. It correlates existing Windows storage telemetry with a lightweight periodic inventory query and shows an actionable alert when a problem is detected.

The first release intentionally does not attempt to be a SMART suite, filesystem repair tool, surface scanner, or ETW performance profiler.

## Runtime model

The application starts at interactive logon, creates one tray icon, and keeps the main window hidden. A background coordinator owns three activities:

1. Event-log watchers read the System channel plus the NTFS and, when the current user can access it, Storport Operational channels for disk, storage-controller, NTFS, Plug and Play, and USB-storage providers. Broken subscriptions are retried after a short backoff; inaccessible or disabled channels are skipped without disabling the other watchers.
2. A periodic inventory refresh first queries current `Win32_PnPEntity` problem codes, then `MSFT_Disk` and `Win32_DiskDrive`, merging Windows health, operational status, bus, volume, PNP, SCSI-port, and PDO metadata.
3. A lightweight `IOCTL_STORAGE_PREDICT_FAILURE` request is attempted against each physical disk. Unsupported devices and USB bridges are reported as unavailable, not unhealthy.

The event watchers start independently so a WMI failure cannot disable telemetry. Observation processing waits briefly for the first inventory when possible, preserving device attribution without making event monitoring depend on WMI. Plug-and-Play notifications request a debounced refresh with a minimum interval so a burst or flap does not cause repeated controller queries.

## Alert policy

Events are normalized into observations with a timestamp, provider, event ID, rule, severity, and best-effort disk/volume/RaidPort identity. At startup, the event-log query covers the full period since the current Windows boot. Entries before that boot are ignored, and active incidents left over from an earlier boot are resolved and excluded from the current assessment. Repeated rules alert only after the configured threshold; immediate rules alert on the first occurrence. Health observations are emitted on a transition, not on every periodic poll.

The status window assigns active incidents one of three user-facing risk levels. Benign means no active issue is recorded. Survivable covers warnings and recoverable errors such as retries, disconnects, and controller timeouts. Critical covers Windows reports associated with corruption, bad blocks, unhealthy media, hardware I/O failure, or predicted failure; it warns that data loss may be imminent or may already have occurred. Acknowledging an incident does not reduce its risk classification, and resolved incidents retain their historical severity. The status and alert windows follow Windows' application light/dark theme and refresh when Windows broadcasts a theme-setting change.

Critical observations bypass the repeated-event threshold, and an escalation to a higher severity bypasses the normal alert cooldown. A live alert restores a topmost alert window and also raises a notification-area balloon so a serious issue cannot remain only as a changed tray icon.

Incident evidence is deduplicated by event record ID where available, and by a timestamp/rule/device fallback otherwise. Incident state is cloned for UI consumers, retained in bounded history, and saved with a short debounce to avoid writing JSON for every individual event. Users can acknowledge an alert or resolve it from either the alert window or the status window. Unresolved incidents with no new evidence for 24 hours are auto-resolved.

Storport reset events usually identify only `\\Device\\RaidPortN`. When Windows exposes `Win32_DiskDrive.SCSIPort`, that number is used to correlate the event with an `MSFT_Disk` snapshot; otherwise the incident is retained as an unattributed storage-port issue rather than guessed onto a disk.

USB inventory diffs track repeated disappearance/reappearance of the same stable device identity. Three flaps in ten minutes produce a dedicated USB flapping incident. The lightweight prediction IOCTL is skipped for USB devices because bridges vary and probing can wake sleeping external disks; its supported/unsupported/access-denied result is shown once per non-USB device in the status window and log.

Kernel-PnP and UMDF rules require storage-shaped instance metadata (`USBSTOR\\`, `SCSI\\Disk`, or a USB VID with the Windows disk class GUID). NTFS event 98 is kept only when its text indicates an actionable offline/full-check/corruption condition.

The separate PnP inventory catches present USB, disk, and storage-controller failures even when no disk was enumerated and no matching event was logged. This includes code 43 / device descriptor failures present before application startup. Disconnected remembered devices and unrelated non-USB device classes are excluded. Unknown USB endpoints are not assumed to be disks and are never probed through physical-drive IOCTLs. Each observation includes the name, instance ID, class, problem code, and timestamp. Multiple failed endpoints remain separate incidents even when sampled simultaneously.

Current PnP failures are observed on every refresh, with existing incident cooldowns limiting popups. This prevents persistent faults from aging out and restores an alert at the next refresh if it was manually cleared while the fault remained. Absence alone does not prove recovery: alerts remain available for investigation until cleared or stale. A failed PnP query raises a monitoring-coverage warning. The status window's Refresh button requests a hardware refresh, and incident details are visible there as well as in alerts and diagnostic logs.

## Privileges and packaging

The application manifest is `asInvoker`; it does not request UAC elevation. The MSI is per-machine and therefore normally requires elevation during installation. The installer is built with WixSharp and WiX 4, installs the self-contained x64 application, creates a Start Menu shortcut, and registers startup under `HKLM`.

Windows controls notification-area placement. The application supplies a colored disk icon and users can pin it in the taskbar notification-area settings, but the program cannot force placement beside Safely Remove Hardware.

## Deliberate limitations

- No raw ETW tracing, so arbitrary slow NTFS operations are outside the first release.
- No automatic `chkdsk`, SMART self-test, surface scan, repair, or data modification.
- SMART passthrough is not attempted; USB enclosure support therefore depends on Windows telemetry and the bridge's exposed capabilities.
- Device identity and controller correlation are best effort because Windows event payloads vary by driver and hardware.
