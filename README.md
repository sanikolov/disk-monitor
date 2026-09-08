# Disk Monitor

Disk Monitor is a small Windows tray utility that watches Windows storage telemetry and warns when a disk or USB storage device starts reporting trouble.

It is open-source software distributed under the [MIT License](LICENSE.txt).

The first release deliberately stays user-mode and conservative:

- It watches the Windows System event log for disk, storage-controller, NTFS, Plug and Play, and USB-storage events.
- It refreshes the Windows storage inventory periodically and checks the health state exposed by Windows.
- It observes the NTFS and Storport Operational channels in addition to the System channel, and detects repeated USB disappearance/reappearance through inventory diffs.
- It opportunistically asks a physical disk whether it predicts failure. Unsupported USB bridges and drivers are treated as unavailable, not unhealthy.
- It does not write to disks, run `chkdsk`, scan the surface, run SMART self-tests, or upload telemetry.
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
