$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishPath = Join-Path $repoRoot 'artifacts\publish\DiskMonitor'
$msiPath = Join-Path $repoRoot 'artifacts\msi'
$minimumWixVersion = [Version]'4.0.1'

$wixCommand = Get-Command wix -ErrorAction SilentlyContinue
if ($null -eq $wixCommand) { throw "WiX CLI $minimumWixVersion or newer is required. Install it with: dotnet tool install --global wix --version $minimumWixVersion" }
$detectedWixText = (& $wixCommand.Source --version 2>&1 | Select-Object -First 1).ToString().Trim()
try {
    $detectedWixVersion = [Version](($detectedWixText -split '\+')[0])
}
catch {
    throw "Could not parse the installed WiX version '$detectedWixText'."
}
if ($detectedWixVersion -lt $minimumWixVersion) {
    throw "WiX CLI $minimumWixVersion or newer is required; detected '$detectedWixText'."
}
$wixExtensions = (& $wixCommand.Source extension list -g 2>&1 | Out-String)
foreach ($extensionName in @('WixToolset.UI.wixext', 'WixToolset.Util.wixext')) {
    $extensionMatch = [regex]::Match($wixExtensions, "$([regex]::Escape($extensionName))\s+(\d+\.\d+\.\d+)")
    if (-not $extensionMatch.Success) {
        throw "WiX extension $extensionName $minimumWixVersion or newer is required. Install it with: wix extension add -g $extensionName/$minimumWixVersion"
    }

    try {
        $detectedExtensionVersion = [Version]$extensionMatch.Groups[1].Value
    }
    catch {
        throw "Could not parse the installed $extensionName version."
    }

    if ($detectedExtensionVersion -lt $minimumWixVersion) {
        throw "WiX extension $extensionName $minimumWixVersion or newer is required; detected '$detectedExtensionVersion'."
    }
}

if (Test-Path -LiteralPath $publishPath) { Remove-Item -LiteralPath $publishPath -Recurse -Force }
if (Test-Path -LiteralPath $msiPath) { Remove-Item -LiteralPath $msiPath -Recurse -Force }

New-Item -ItemType Directory -Force -Path $publishPath, $msiPath | Out-Null

dotnet test (Join-Path $repoRoot 'tests\DiskMonitor.Tests\DiskMonitor.Tests.csproj') `
    --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }

dotnet publish (Join-Path $repoRoot 'src\DiskMonitor\DiskMonitor.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

dotnet run --project (Join-Path $repoRoot 'installer\DiskMonitor.Installer.csproj') `
    --configuration Release
if ($LASTEXITCODE -ne 0) { throw "MSI build failed with exit code $LASTEXITCODE" }

$msiFile = Join-Path $msiPath 'DiskMonitor.msi'
if (-not (Test-Path -LiteralPath $msiFile -PathType Leaf)) { throw "MSI build did not produce $msiFile" }

# WixSharp's MajorUpgrade support intentionally defines REINSTALLMODE, so ICE40
# is not actionable for this package. All other standard MSI validation runs.
& $wixCommand.Source msi validate -sice ICE40 $msiFile
if ($LASTEXITCODE -ne 0) { throw "MSI validation failed with exit code $LASTEXITCODE" }

Write-Host "MSI output: $msiPath"
