$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$cacheRoot = Join-Path $projectRoot '.cache'
$env:DOTNET_CLI_HOME = Join-Path $cacheRoot 'dotnet-home'
$env:APPDATA = Join-Path $cacheRoot 'appdata'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'

Push-Location $projectRoot
try {
    dotnet restore src/PocketRecomp.Cli/PocketRecomp.Cli.csproj --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'CLI restore failed.' }
    dotnet restore tests/PocketRecomp.Tests/PocketRecomp.Tests.csproj --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }
    dotnet build src/PocketRecomp.Cli/PocketRecomp.Cli.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed.' }
    dotnet build tests/PocketRecomp.Tests/PocketRecomp.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    dotnet tests/PocketRecomp.Tests/bin/Release/net9.0/PocketRecomp.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally {
    Pop-Location
}
