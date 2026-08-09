@echo off
chcp 65001 >nul
title BigWorld Server Stopper

echo Stopping all BigWorld servers...

REM ---- 1. Graceful shutdown via central --------------------------------------
REM Ask central to orchestrate a graceful stop: gateway notifies clients, world
REM flushes players to dbproxy, each server exits in dependency order.
echo [1/3] Requesting graceful shutdown ...
go run ./shutdown
if errorlevel 1 (
    echo   [WARN] Graceful shutdown trigger failed - central may be down.
    echo          Falling through to force-kill.
    goto force
)

REM ---- 2. Wait for all servers to exit (poll ports, up to 60s) ---------------
echo [2/3] Waiting for servers to exit ...
powershell -NoProfile -Command "$ports=9000,8000,9100,9200,9300; $n=60; for($i=0;$i -lt $n;$i++){ $up=0; foreach($p in $ports){ try{$c=New-Object Net.Sockets.TcpClient;$c.Connect('127.0.0.1',$p);$c.Close();$up++}catch{} }; if($up -eq 0){ Write-Host 'All ports closed.'; exit 0 }; Start-Sleep 1 }; exit 1"
if errorlevel 1 (
    echo   [WARN] Some servers still listening after 60s - force killing.
    goto force
)

echo.
echo All servers stopped gracefully.
echo   Central   :9000 - stopped
echo   Gateway   :8000 - stopped
echo   World     :9100 - stopped
echo   Login     :9200 - stopped
echo   DBProxy   :9300 - stopped
echo.
exit /b

:force
echo.
echo Force-killing remaining BigWorld processes...
:: Kill Go processes running server code
taskkill /f /im go.exe 2>nul
taskkill /f /im main.exe 2>nul
taskkill /f /im central.exe 2>nul
taskkill /f /im world.exe 2>nul
taskkill /f /im gateway.exe 2>nul
taskkill /f /im login.exe 2>nul
taskkill /f /im dbproxy.exe 2>nul

:: Kill any lingering server processes by port (Windows)
for %%p in (9000 8000 9100 9200 9300) do (
    netstat -ano | findstr "%%p" >nul 2>nul
    if not errorlevel 1 (
        for /f "tokens=5" %%a in ('netstat -ano ^| findstr "%%p"') do (
            taskkill /f /pid %%a 2>nul
        )
    )
)

echo All servers stopped (force kill).
echo   Central   :9000 - stopped
echo   Gateway   :8000 - stopped
echo   World     :9100 - stopped
echo   Login     :9200 - stopped
echo   DBProxy   :9300 - stopped
echo.
