# Builds the plugin and installs it for Quantower's next start.
#   .\deploy.ps1              release build
#   .\deploy.ps1 -TestHooks   adds the file trigger that drops the socket on demand
param([switch]$TestHooks)

$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
$project = Join-Path $PSScriptRoot 'ScalpFeedVendor\ScalpFeedVendor.csproj'
$output = Join-Path $PSScriptRoot 'ScalpFeedVendor\bin\Release\ScalpFeedVendor.dll'
$targetDir = 'C:\Quantower\Settings\Scripts\Vendors\ScalpFeedVendor'
$target = Join-Path $targetDir 'ScalpFeedVendor.dll'

& $dotnet build $project -c Release -v quiet -nologo "-p:TestHooks=$($TestHooks.IsPresent.ToString().ToLower())" --no-incremental
if ($LASTEXITCODE -ne 0) { throw 'Build failed; nothing was deployed.' }

New-Item -ItemType Directory -Force $targetDir | Out-Null

# Clear out earlier parked copies. One still in use by a running Quantower just stays for now.
Get-ChildItem $targetDir -Filter '*.old' -ErrorAction SilentlyContinue | ForEach-Object {
    try { Remove-Item $_.FullName -Force -Confirm:$false } catch { }
}

# A running Quantower locks the DLL against overwriting, but Windows still allows a rename.
if (Test-Path $target) {
    try {
        Copy-Item $output $target -Force
        "Installed over the existing DLL."
    }
    catch {
        Rename-Item $target "ScalpFeedVendor.$(Get-Date -Format yyyyMMddHHmmss).old"
        Copy-Item $output $target
        "Quantower has the old DLL open. Parked it; the new one loads when Quantower restarts."
    }
}
else {
    Copy-Item $output $target
    "Installed."
}

Get-ChildItem $targetDir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
