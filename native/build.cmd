@echo off
rem Builds native\lib\dqnative.dll. Needs MinGW-w64 gcc on PATH, or run from a Visual Studio Developer Command Prompt (cl).
cd /d "%~dp0"
if not exist lib mkdir lib
where gcc >nul 2>nul
if not errorlevel 1 goto usegcc
where cl >nul 2>nul
if not errorlevel 1 goto usecl
echo No C compiler found. Install MinGW-w64 (gcc) or use a Visual Studio Developer Command Prompt.
exit /b 1
:usegcc
gcc -shared -O2 -o lib\dqnative.dll dqnative.c
exit /b %errorlevel%
:usecl
cl /nologo /LD /O2 /Fo:lib\ dqnative.c /Fe:lib\dqnative.dll /link /IMPLIB:lib\dqnative.lib
exit /b %errorlevel%
