@echo off
rem Windows cmd 入口（薄壳）：逻辑在 build\Program.cs。
rem   build.cmd [选项]
dotnet run --project "%~dp0build\Build.csproj" -- %*
exit /b %ERRORLEVEL%
