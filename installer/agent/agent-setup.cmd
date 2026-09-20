@echo off
rem Runs as SYSTEM (deferred MSI custom action) right after files are copied.
rem Brings up the standalone mini-CA, registers autostart tasks and issues the machine certificate.
rem NOTE: keep this file ASCII-only: cmd.exe reads .cmd in the OEM codepage.
setlocal
set "DIR=%~dp0"
set "LOG=%ProgramData%\RemoteControl\msi-setup.log"
if not exist "%ProgramData%\RemoteControl" mkdir "%ProgramData%\RemoteControl"
echo [%DATE% %TIME%] MSI setup start >> "%LOG%"

rem HTTP.sys: allow binding ports 80/8555
netsh http add urlacl url=http://+:80/ user=SYSTEM >> "%LOG%" 2>&1
netsh http add urlacl url=http://+:8555/ user=SYSTEM >> "%LOG%" 2>&1

rem CA task: start at boot as SYSTEM
schtasks /create /tn RemoteControlLabCa /tr "\"%DIR%ca\labca.exe\"" /sc onstart /ru SYSTEM /rl HIGHEST /f >> "%LOG%" 2>&1
rem Machine certificate enrollment at boot (idempotent)
schtasks /create /tn RemoteControlEnrollMachine /tr "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%DIR%enroll-cert.ps1\" -Kind Machine" /sc onstart /ru SYSTEM /rl HIGHEST /f >> "%LOG%" 2>&1

rem Start the CA and issue the certificate right away
schtasks /end /tn RemoteControlLabCa >> "%LOG%" 2>&1
schtasks /run /tn RemoteControlLabCa >> "%LOG%" 2>&1
ping -n 7 127.0.0.1 >nul
schtasks /run /tn RemoteControlEnrollMachine >> "%LOG%" 2>&1
ping -n 26 127.0.0.1 >nul

echo [%DATE% %TIME%] MSI setup done >> "%LOG%"
endlocal
