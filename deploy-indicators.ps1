# Builds the chart indicators and installs them where Quantower looks for them.
#   .\deploy-indicators.ps1
$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
$project = Join-Path $PSScriptRoot 'ScalpFeedIndicators\ScalpFeedIndicators.csproj'
$output = Join-Path $PSScriptRoot 'ScalpFeedIndicators\bin\Release\KeyMovingAverages.dll'
$targetDir = 'C:\Quantower\Settings\Scripts\Indicators\KeyMovingAverages'
$target = Join-Path $targetDir 'KeyMovingAverages.dll'

& $dotnet build $project -c Release -v quiet -nologo --no-incremental
if ($LASTEXITCODE -ne 0) { throw 'Build failed; nothing was deployed.' }

New-Item -ItemType Directory -Force $targetDir | Out-Null
Get-ChildItem $targetDir -Filter '*.old' -ErrorAction SilentlyContinue | ForEach-Object {
    try { Remove-Item $_.FullName -Force -Confirm:$false } catch { }
}

# A running Quantower may hold the DLL open; Windows still allows a rename.
if (Test-Path $target) {
    try {
        Copy-Item $output $target -Force
        "Installed over the existing DLL."
    }
    catch {
        Rename-Item $target "KeyMovingAverages.$(Get-Date -Format yyyyMMddHHmmss).old"
        Copy-Item $output $target
        "Quantower has the old DLL open. Parked it; the new one loads when Quantower restarts."
    }
}
else {
    Copy-Item $output $target
    "Installed."
}

Get-ChildItem $targetDir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
