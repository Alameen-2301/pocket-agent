<#
.SYNOPSIS
  Tests and builds Pocket Agent for Windows 10/11 (x64).

.DESCRIPTION
  1. Rust engine: unit tests, then a release build of pocket_agent_core.dll.
  2. Core tests: the agent loop, model providers, voice, safety rules and the MCP server
     running on the real Rust engine (a self-contained runner, no test framework needed).
  3. ABI smoke test of the Rust DLL from .NET.
  4. Publishes a self-contained single-file PocketAgent.Windows.exe (no .NET install needed
     on the target PC), puts pocket_agent_core.dll beside it and zips both.
  5. Optionally builds an installer with Inno Setup 6 or later.

.PARAMETER SkipTests
  Skip steps 1-3's tests (the Rust DLL is still built).

.PARAMETER Installer
  Also build dist\PocketAgent-Setup.exe (needs Inno Setup: https://jrsoftware.org/isinfo.php).

.EXAMPLE
  .\build-windows.ps1
  .\build-windows.ps1 -Installer
#>
param(
    [switch]$SkipTests,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Check([string]$what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." } }

foreach ($tool in 'cargo', 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool was not found. Install Rust (https://rustup.rs, MSVC x64 toolchain) and the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0), then open a new PowerShell window."
    }
}

$dll = 'rust-core/target/release/pocket_agent_core.dll'

if (-not $SkipTests) {
    Step 'Rust engine: tests'
    cargo test --manifest-path rust-core/Cargo.toml
    Check 'Rust tests'
}

Step 'Rust engine: release build'
cargo build --release --manifest-path rust-core/Cargo.toml
Check 'Rust build'
if (-not (Test-Path $dll)) { throw "Expected $dll after the Rust build." }

if (-not $SkipTests) {
    Step 'Core tests (agent loop, providers, voice, safety, MCP on the real engine)'
    dotnet run --project tests/PocketAgent.Core.Tests/PocketAgent.Core.Tests.csproj -c Release
    Check 'Core tests'

    Step 'Rust DLL ABI smoke test'
    $smoke = 'dist/CoreSmoke'
    dotnet publish src/CoreSmoke/CoreSmoke.csproj -c Release -r win-x64 --self-contained false -o $smoke
    Check 'Smoke test build'
    Copy-Item $dll $smoke -Force
    dotnet "$smoke/CoreSmoke.dll"
    Check 'Rust DLL ABI smoke test'
}

Step 'Publishing the app (self-contained, single file)'
$out = 'dist/PocketAgent-Windows'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish src/PocketAgent.Windows/PocketAgent.Windows.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $out
Check 'App publish'
Copy-Item $dll $out -Force

$zip = 'dist/PocketAgent-Windows-portable.zip'
Compress-Archive -Path "$out/*" -DestinationPath $zip -Force
Write-Host "`nPortable app: $zip" -ForegroundColor Green
Write-Host "Run $out/PocketAgent.Windows.exe (keep pocket_agent_core.dll in the same folder)."

if ($Installer) {
    Step 'Installer (Inno Setup)'
    $iscc = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Inno Setup*\ISCC.exe", "$env:ProgramFiles\Inno Setup*\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup*\ISCC.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup (ISCC.exe) was not found. Install it from https://jrsoftware.org/isinfo.php or run: winget install JRSoftware.InnoSetup' }
    & $iscc.FullName installer/PocketAgent.iss
    Check 'Installer build'
    Write-Host 'Installer: dist/PocketAgent-Setup.exe' -ForegroundColor Green
}
