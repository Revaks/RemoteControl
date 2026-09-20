@echo off
rem Stops the standalone mini-CA and removes autostart tasks on MSI uninstall.
rem NOTE: keep this file ASCII-only: cmd.exe reads .cmd in the OEM codepage.
schtasks /end /tn RemoteControlLabCa >nul 2>&1
schtasks /delete /tn RemoteControlLabCa /f >nul 2>&1
schtasks /end /tn RemoteControlEnrollMachine >nul 2>&1
schtasks /delete /tn RemoteControlEnrollMachine /f >nul 2>&1
taskkill /f /im labca.exe >nul 2>&1
exit /b 0
