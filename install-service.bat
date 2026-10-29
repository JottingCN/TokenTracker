@echo off
setlocal EnableExtensions

set "SERVICE_NAME=TokenTracker"
set "DISPLAY_NAME=Token Tracker"
set "MODE=%~1"
set "EXE=%~2"

if not defined EXE set "EXE=%~dp0src\TokenTracker.Server\bin\Release\net10.0\win-x64\publish\TokenTracker.Server.exe"

if /I "%MODE%"=="install" goto :install
if /I "%MODE%"=="uninstall" goto :uninstall
call :usage
exit /b 1

:install
call :require_admin
if errorlevel 1 exit /b 1

if not exist "%EXE%" (
    echo [ERROR] Server executable not found:
    echo         %EXE%
    echo         Publish it first:
    echo         dotnet publish src\TokenTracker.Server -c Release -r win-x64
    exit /b 1
)

echo [INFO] Installing service "%SERVICE_NAME%" ...
echo [INFO] Executable: %EXE%

call :stop_and_delete

echo [INFO] Writing machine environment for data and session titles...
setx /M TOKENTRACKER_DATA_DIR "%LOCALAPPDATA%\TokenTracker" >nul
setx /M TOKENTRACKER_SESSION_STATE_DIR "%USERPROFILE%\.copilot\session-state" >nul
setx /M TOKENTRACKER_AGENT_HOST_DB "%APPDATA%\Code\User\globalStorage\agent-host.db" >nul

sc create "%SERVICE_NAME%" binPath= "\"%EXE%\"" start= auto DisplayName= "%DISPLAY_NAME%" >nul
if errorlevel 1 (
    echo [ERROR] "sc create" failed.
    exit /b 1
)

sc description "%SERVICE_NAME%" "TokenTracker OTLP receiver and realtime dashboard" >nul
sc failure "%SERVICE_NAME%" reset= 86400 actions= restart/5000/restart/10000/restart/30000 >nul

echo [INFO] Starting service...
sc start "%SERVICE_NAME%" >nul
if errorlevel 1 (
    echo [ERROR] Service failed to start. Run "sc query %SERVICE_NAME%" and check
    echo         Event Viewer ^> Windows Logs ^> Application, or make sure port 4318
    echo         is not used by another TokenTracker instance.
    exit /b 1
)

echo [OK] Service installed and running.
echo      Dashboard: http://127.0.0.1:4318/
echo      Note: stop any console TokenTracker.Server.exe / WinUI app that uses port 4318.
exit /b 0

:uninstall
call :require_admin
if errorlevel 1 exit /b 1

call :stop_and_delete
echo [OK] Service "%SERVICE_NAME%" removed.
exit /b 0

:stop_and_delete
sc query "%SERVICE_NAME%" >nul 2>&1
if errorlevel 1 exit /b 0

echo [INFO] Stopping service...
sc stop "%SERVICE_NAME%" >nul 2>&1
call :wait_stopped

echo [INFO] Deleting service...
sc delete "%SERVICE_NAME%" >nul 2>&1
timeout /t 2 /nobreak >nul
exit /b 0

:wait_stopped
for /l %%i in (1,1,15) do (
    sc query "%SERVICE_NAME%" 2>nul | findstr /C:"STOPPED" >nul && exit /b 0
    timeout /t 1 /nobreak >nul
)
exit /b 0

:require_admin
net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Administrator privileges required.
    echo         Right-click this script and choose "Run as administrator".
    exit /b 1
)
exit /b 0

:usage
echo Usage:
echo   %~nx0 install ["full path to TokenTracker.Server.exe"]
echo   %~nx0 uninstall
exit /b 0
