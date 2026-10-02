@echo off
rem Builds ..\GeneralsBorderless.exe: the patcher, with dinput8.dll embedded.
rem dinput8.dll is built with Zig 0.16 (ziglang.org), which must be on PATH; without Zig an
rem existing proxy\dinput8.dll is used as it is.
cd /d "%~dp0"
where zig >nul 2>nul
if not errorlevel 1 (
  zig cc -target x86-windows-gnu -O2 -Wall -Wno-unused-parameter -shared -s -o proxy\dinput8.dll proxy\borderless.c proxy\dinput8.def proxy\version.rc -luser32 -lshell32 -ladvapi32
  if errorlevel 1 goto failed
  del /q borderless.lib proxy\borderless.lib proxy\dinput8.lib 2>nul
) else if not exist proxy\dinput8.dll (
  echo Zig is not on PATH and there is no proxy\dinput8.dll yet. Install Zig 0.16 from ziglang.org.
  goto failed
)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /win32manifest:patcher\app.manifest /resource:proxy\dinput8.dll,dinput8.dll /out:..\GeneralsBorderless.exe patcher\Patcher.cs
if errorlevel 1 goto failed
exit /b 0
:failed
rem Keep the window open when double-clicked, but not on a build server.
if not defined CI pause
exit /b 1
