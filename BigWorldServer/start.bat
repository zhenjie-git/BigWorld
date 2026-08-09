@echo off
chcp 65001 >nul
title BigWorld Server

echo ========================================
echo   BigWorld Multi-Process Server Launcher
echo ========================================
echo.

REM ---- MySQL service ---------------------------------------------------------
REM Windows service name for the local MySQL install. Adjust if yours differs.
set MYSQL_SERVICE=MySQL267

echo [0/6] Checking MySQL service "%MYSQL_SERVICE%" ...
net start | findstr /i /c:"%MYSQL_SERVICE%" >nul 2>nul
if %errorlevel% equ 0 (
    echo   MySQL is already running.
) else (
    echo   Starting MySQL service ...
    net start %MYSQL_SERVICE% >nul 2>nul
    if errorlevel 1 (
        echo   [WARN] Could not start MySQL. Run start.bat as Administrator,
        echo          or check that the service name "%MYSQL_SERVICE%" is correct.
    ) else (
        echo   MySQL started.
    )
)

REM Wait until MySQL accepts connections (it may take a few seconds after start).
REM Polls every 1s for up to 30s. A fixed sleep would race a slow mysqld start.
powershell -NoProfile -Command "$p=3306;$n=30;for($i=0;$i -lt $n;$i++){ $up=$false; try{$c=New-Object Net.Sockets.TcpClient;$c.Connect('127.0.0.1',$p);$c.Close();$up=$true}catch{}; if($up){exit 0}; Start-Sleep 1 }; exit 1"
if errorlevel 1 echo   [WARN] MySQL not accepting connections within 30s - continuing anyway.
echo.

echo [1/6] Starting Central Server ...
start "central" /D "%~dp0" cmd /c "go run ./central"

REM "start" returns immediately, but dbproxy/world/login/gateway do a one-shot
REM dial to central on boot that is FATAL if it fails (no retry). go run must
REM compile+link first, so wait until central is actually listening before
REM launching the rest. Polls every 1s for up to 30s.
powershell -NoProfile -Command "$p=9000;$n=30;for($i=0;$i -lt $n;$i++){ $up=$false; try{$c=New-Object Net.Sockets.TcpClient;$c.Connect('127.0.0.1',$p);$c.Close();$up=$true}catch{}; if($up){exit 0}; Start-Sleep 1 }; exit 1"
if errorlevel 1 echo   [WARN] Central not up within 30s - check the central window; continuing anyway.

echo [2/6] Starting DBProxy Server ...
start "dbproxy" /D "%~dp0" cmd /c "go run ./dbproxy"

echo [3/6] Starting World Server ...
start "world" /D "%~dp0" cmd /c "go run ./world"

echo [4/6] Starting Login Server ...
start "login" /D "%~dp0" cmd /c "go run ./login"

echo [5/6] Starting Gateway Server ...
start "gateway" /D "%~dp0" cmd /c "go run ./gateway"

echo.
echo All servers started (run from repo root so common/config.json resolves):
echo   MySQL     :3306
echo   Central   :9000
echo   DBProxy   :9300
echo   World     :9100
echo   Login     :9200
echo   Gateway   :8000
echo.
echo Use stop.bat to shut down all servers.
