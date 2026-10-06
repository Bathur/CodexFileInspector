@echo off
setlocal DisableDelayedExpansion
title Codex File Inspector - HTTP

if not exist "%~dp0CodexFileInspector.exe" (
    echo ERROR: CodexFileInspector.exe was not found beside this launcher.
    echo Keep Start-Http.bat in the complete published application folder.
    echo.
    pause
    exit /b 1
)

echo Starting Codex File Inspector in HTTP mode.
echo Default endpoint: http://127.0.0.1:43127/mcp
echo Press Ctrl+C to stop the server.
echo.

"%~dp0CodexFileInspector.exe" --transport http %*
set "serverExitCode=%ERRORLEVEL%"

echo.
echo Server stopped. Exit code: %serverExitCode%
pause
exit /b %serverExitCode%
