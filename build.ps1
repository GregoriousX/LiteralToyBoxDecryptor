param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.build\cli-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:APPDATA = Join-Path $PSScriptRoot '.build\appdata'
$env:LOCALAPPDATA = Join-Path $PSScriptRoot '.build\localappdata'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $PSScriptRoot '.build\http-cache'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.build\packages'
$env:TEMP = Join-Path $PSScriptRoot '.build\temp'
$env:TMP = $env:TEMP
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $PSScriptRoot '.build\bundle-cache'
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
Push-Location $PSScriptRoot
try {
    dotnet publish src/App/App.csproj -c Release -o dist/Release --configfile NuGet.Config --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Single-file build failed.' }
    dotnet publish src/Cli/Cli.csproj -c Release -o dist/CLI --configfile NuGet.Config --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed.' }
    if (-not $SkipTests) {
        $testOutput = Join-Path $PSScriptRoot ('qa\unit-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
        dotnet run --project tests/Tests.csproj -c Release -- $testOutput
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
} finally { Pop-Location }
