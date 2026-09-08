# MSI builder

This project uses WixSharp 2.x with WiX 4 to author a per-machine MSI. Install WiX 4.0.1 or newer and its UI extension before building:

```powershell
dotnet tool install --global wix --version 4.0.1
wix extension add -g WixToolset.UI.wixext/4.0.1
wix extension add -g WixToolset.Util.wixext/4.0.1
```

The published, self-contained x64 application is supplied by `build.ps1` in `artifacts/publish/DiskMonitor`.

The installer creates:

- `Program Files\Disk Monitor\DiskMonitor.exe` and its runtime files;
- a Start Menu shortcut; and
- an `HKLM` startup entry so the tray application starts at interactive logon.

The application itself declares `asInvoker` and does not request elevation at runtime.
