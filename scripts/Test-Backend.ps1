$ErrorActionPreference = 'Stop'
$taskDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$taskSdk = Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'
if (Test-Path -LiteralPath $taskSdk) { $taskExecutable = $taskSdk }
elseif ($taskDotnet) { $taskExecutable = $taskDotnet.Source }
else { throw 'Install .NET SDK 8 before running backend tests.' }
$taskProject = Join-Path $PSScriptRoot '../backend/Restaurant.Api.Tests/Restaurant.Api.Tests.csproj'
& $taskExecutable test $taskProject -c Release
if ($LASTEXITCODE) { throw 'Backend tests failed.' }
