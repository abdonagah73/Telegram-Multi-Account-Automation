@echo off
title Telegram Multi-Account Automation
color 0b
echo ========================================================
echo   Telegram Multi-Account Automation Platform (.NET 8)
echo ========================================================
echo.
echo Starting backend server on http://localhost:5000 ...
echo.

start "" "http://localhost:5000"

cd /d "%~dp0TelegramAutomationApp\Backend"
"d:\dotnet-sdk\dotnet.exe" run --urls "http://localhost:5000"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Trying standard dotnet command...
    dotnet run --urls "http://localhost:5000"
)

pause
