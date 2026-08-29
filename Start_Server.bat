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
if exist "d:\dotnet-sdk\dotnet.exe" (
    "d:\dotnet-sdk\dotnet.exe" run --urls "http://localhost:5000"
) else (
    dotnet run --urls "http://localhost:5000"
)

pause
