@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0brightness.ps1" %*
exit /b %ERRORLEVEL%
