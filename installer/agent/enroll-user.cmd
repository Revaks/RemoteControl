@echo off
rem Issues the USER (client) certificate for the operator console.
rem By default - from the local CA (standalone mode).
rem To connect to another server:
rem   enroll-user.cmd -CaUrl http://server/ -EnrollUrl http://server:8555/
rem NOTE: keep this file ASCII-only: cmd.exe reads .cmd in the OEM codepage.
set "DIR=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%DIR%enroll-cert.ps1" -Kind User %*
if errorlevel 1 pause
