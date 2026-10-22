param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0, then run Build.cmd again.'
}
if (Get-Process -Name CodexUsageWidget -ErrorAction SilentlyContinue) {
    throw 'Quit the running widget from its menu or tray icon, then build again.'
}
& dotnet run --project (Join-Path $projectRoot 'tests\UsageChecks\UsageChecks.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Usage checks failed. Build stopped.' }
& dotnet run --project (Join-Path $projectRoot 'tests\WindowChecks\WindowChecks.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Window checks failed. Build stopped.' }
& dotnet publish (Join-Path $projectRoot 'src\TokenUsageTracker\TokenUsageTracker.csproj') --configuration Release --runtime $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None --output (Join-Path $projectRoot 'dist')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Close the widget if it is running, and check the build output above.' }
Write-Host "Portable widget: $(Join-Path $projectRoot 'dist\CodexUsageWidget.exe')"
