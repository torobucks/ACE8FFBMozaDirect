# Builds AceBridge and assembles a ready-to-zip release folder in .\release (gitignored).
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1                 # framework-dependent (needs .NET 8 Runtime)
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -SelfContained  # bundles the .NET runtime (bigger, no install needed)
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -SdkDll "C:\...\MOZA_SDK.dll"
#       MOZA_SDK.dll is copied next to AceBridge.exe; auto-detected from Downloads if not given.
param(
  [switch]$SelfContained,
  [string]$Version = "0.5.0",
  [string]$SdkDll = $env:MOZA_SDK_DLL
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$name = "AceFFB-$Version"
$out  = Join-Path $root "release\$name"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

# Prefer a locally-installed .NET SDK (e.g. ~\.dotnet from dotnet-install.ps1), else PATH.
$dotnet = (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
if (-not (Test-Path $dotnet)) { $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source }
if (-not $dotnet) { throw "No .NET SDK found. Install it (https://dot.net) or run dotnet-install.ps1 to ~\.dotnet." }

$sc = if ($SelfContained) { "true" } else { "false" }
& $dotnet publish (Join-Path $root "bridge\AceBridge\AceBridge.csproj") -c Release -r win-x64 --self-contained $sc `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $out "AceBridge")
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Remove-Item (Join-Path $out "AceBridge\*.pdb") -ErrorAction SilentlyContinue

# MOZA_SDK.dll must sit next to AceBridge.exe. Auto-detect if not given.
if (-not $SdkDll) {
  $cands = @(
    (Join-Path $env:USERPROFILE 'Downloads\MOZA_SDK_1.0.0.6\MOZA_SDK_1.0.0.6\MOZA_SDK\x64-release\bin\MOZA_SDK.dll')
  )
  foreach ($c in $cands) { if (Test-Path -LiteralPath $c) { $SdkDll = $c; break } }
  if (-not $SdkDll -and (Get-ChildItem -Path $env:USERPROFILE\Downloads -Recurse -Filter 'MOZA_SDK.dll' -ErrorAction SilentlyContinue)) {
    $SdkDll = (Get-ChildItem -Path $env:USERPROFILE\Downloads -Recurse -Filter 'MOZA_SDK.dll' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
  }
}
if ($SdkDll -and (Test-Path -LiteralPath $SdkDll)) {
  Copy-Item -LiteralPath $SdkDll (Join-Path $out "AceBridge\MOZA_SDK.dll")
  Write-Host "Copied MOZA_SDK.dll -> $out\AceBridge"
} else {
  Write-Warning "MOZA_SDK.dll not found. AceBridge needs it next to AceBridge.exe at runtime; pass -SdkDll <path>."
}

Copy-Item (Join-Path $root "config\ffb-profiles.json") (Join-Path $out "AceBridge\ffb-profiles.json")
New-Item -ItemType Directory -Force (Join-Path $out "Mods") | Out-Null
Copy-Item -Recurse (Join-Path $root "mod\AceTelemetry") (Join-Path $out "Mods\AceTelemetry")
Copy-Item -Recurse (Join-Path $root "examples") (Join-Path $out "examples")
Copy-Item (Join-Path $root "scripts\Start-AceBridge.cmd") $out
New-Item -ItemType Directory -Force (Join-Path $out "scripts") | Out-Null
foreach ($f in "Install-AceFFB.ps1", "Uninstall-AceFFB.ps1", "AceFFB-Common.ps1") {
  Copy-Item (Join-Path $root "scripts\$f") (Join-Path $out "scripts")
}
# Double-click installers at the zip root
Copy-Item (Join-Path $root "Install-AceFFB.cmd"), (Join-Path $root "Uninstall-AceFFB.cmd") $out
Copy-Item (Join-Path $root "README.md"), (Join-Path $root "LICENSE") $out

$zip = Join-Path $root "release\$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
Write-Host "Release folder: $out"
Write-Host "Release zip:    $zip"
