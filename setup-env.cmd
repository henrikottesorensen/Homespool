@echo off
REM Runs setup-env.ps1 on Windows, from a double-click or a plain command prompt.
REM
REM This exists for one reason: PowerShell refuses to run script FILES under the default execution
REM policy, so `.\setup-env.ps1` fails with "cannot be loaded because running scripts is disabled on
REM this system" on a machine nobody has configured. cmd.exe has no such policy, and
REM -ExecutionPolicy Bypass lifts it for this one process only - no machine-wide change, nothing to
REM undo, and no asking somebody to weaken a security setting to run a setup script.
REM
REM It carries NO logic of its own, deliberately. Detection lives in setup-env.ps1, and every
REM decision lives in setup-env.sh; a launcher that started making choices would be a third place
REM for them to disagree. If this file ever needs a second meaningful line, something has gone wrong.
REM
REM %~dp0 is this file's own directory, so it works from anywhere; %* passes --dry-run, --set and
REM the rest straight through.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-env.ps1" %*
