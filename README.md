# Disk Monitor (LLM assisted in the creation of this utility)

Disk Monitor is a small Windows tray utility that watches Windows storage telemetry and warns when a disk or USB storage device starts reporting trouble.

It is open-source software distributed under the [MIT License](LICENSE.txt).

The first release deliberately stays user-mode and conservative:

- It watches the Windows System, NTFS Operational, Storage ClassPnP Operational, and available Storport Operational logs for disk, storage-controller, NTFS, Plug and Play, and USB-storage events.
- Repeated NTFS slow-I/O warnings are reported for any disk bus, including SATA. Repeated failed non-read/write storage commands are reported without treating a rejected device probe as lost user data.
- Storage command details decode SCSI commands, SRB flags, sense qualifiers, and Windows status codes. Related ClassPnP read/write, capacity, power, and control failures are captured alongside the warning. The local rolling log includes raw event XML, publisher execution IDs, the latest device snapshot, and the timing of Disk Monitor's hardware probes.
- It refreshes the Windows storage inventory periodically and checks the health state exposed by Windows.
- It checks present USB devices, disks, and storage controllers for Plug and Play problem codes, including descriptor failures that prevent a drive from appearing in the disk inventory. Unidentified USB devices are reported without assuming they are disks.
- It observes the NTFS and Storport Operational channels in addition to the System channel, and detects repeated USB disappearance/reappearance through inventory diffs.
- It opportunistically asks a physical disk whether it predicts failure. Unsupported USB bridges and drivers are treated as unavailable, not unhealthy.
- It does not write to disks, run `chkdsk`, scan the surface, run SMART self-tests, or upload telemetry.
- The status window shows when inventory last succeeded, which event channels or PnP checks are unavailable, and whether diagnostic logging has stopped. A heartbeat keeps the tray warning state current even when an inventory request stalls. WMI and device probes run in timeboxed child processes so a stuck driver cannot permanently block later inventory scans.
- It runs as a standard user. The MSI is machine-wide and therefore normally requires UAC during installation.

The application uses .NET 8 WinForms. The MSI is authored with WixSharp 2.x and WiX 4.

Windows may place newly installed notification icons in the tray overflow area. The user can pin Disk Monitor in the taskbar notification-area settings; Windows does not allow an application to force a position next to Safely Remove Hardware.

## Repository layout

```text
src/DiskMonitor/                 Tray application
installer/                       WixSharp MSI builder
tests/DiskMonitor.Tests/         Parser and inventory unit tests
docs/DESIGN.md                   Scope, signals, alert policy, and limitations
build.ps1                        Publish the app and build the MSI
artifacts/                       Generated publish and MSI output (ignored)
```

## Build and package

Prerequisites:

- Windows 10/11 x64
- .NET 8 SDK
- .NET Framework 4.7.2 targeting pack
- WiX 4 (`dotnet tool install --global wix`)
- WiX UI extension (`wix extension add -g WixToolset.UI.wixext/4.0.1`)
- WiX Util extension (`wix extension add -g WixToolset.Util.wixext/4.0.1`)

From the repository root:

```powershell
.\build.ps1
```

The script runs the unit tests, publishes the application to `artifacts/publish/DiskMonitor`, and places the MSI in `artifacts/msi`.

The repository intentionally does not include generated binaries. Hardware and event-log testing should be performed only after reviewing the implementation. The application can be closed by the installer through its hidden notification window, allowing upgrades and removal without leaving the tray process running.

The initial thresholds and polling intervals are intentionally compiled defaults for this small first release; they are documented in `docs/DESIGN.md` and are not yet exposed as a settings UI.

## Investigating storage warnings

Use **Open data folder** in the status window to find `%LOCALAPPDATA%\DiskMonitor`. Preserve `monitor.log`, `storage-events.log`, their `.1` archives, any `probe-*.log` files, and `state.json` soon after a warning. Each log rotates at 2 MiB and keeps one archive. Raw events use a separate file so startup replay cannot displace probe timing and monitoring-health entries. Everything stays local.

`Storage event telemetry:` lines in `storage-events.log` contain a JSON object with the exact event time, receipt time, channel/record ID, decoded details, raw XML, and the matched device's serial, firmware, PnP ID, bus, volumes, and SCSI address. Records are logged even below the popup threshold or during alert cooldown. Events retained since boot are collected again on application restart so an upgrade can add diagnostics for previously seen warnings. Replayed records may precede the attached inventory snapshot; its timestamp and any inventory problem are included explicitly. Raw XML is capped at 64 KiB with an explicit truncation flag.

For example, CDB `000000000000` means **TEST UNIT READY**. Valid sense `02/04/00` means **not ready, cause not reportable**; it cannot establish whether the drive was sleeping or has a power, connection, enclosure, or drive fault. Look for related read/write, capacity, power, removal, or reset events with the same serial and nearby event times. The event's execution PID is publisher context and can be zero or reused; it does not prove which application initiated the command. Probe start/exit entries in `monitor.log` and per-WMI-query timestamps plus legacy device snapshots in `probe-inventory.log` support timing comparisons with Disk Monitor's own activity, without triggering additional hardware queries.
