#!/usr/bin/env pwsh
# Windows PowerShell / pwsh 入口（薄壳）：逻辑在 build/Program.cs。
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   pwsh build.ps1 --no-deploy
$ErrorActionPreference = 'Stop'

& dotnet run --project (Join-Path $PSScriptRoot 'build/Build.csproj') -- @args
exit $LASTEXITCODE
