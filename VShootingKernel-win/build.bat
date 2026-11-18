@echo off
rem Builds VShootingKernel.dll (x64), the board emulator and VDartsMod, and installs them into the game folder.
rem The game folder (the one holding VDartsH2_V1.exe) must be given as an absolute path in VDARTS_GAME_DIR.
setlocal
cd /d "%~dp0"
if not defined VDARTS_GAME_DIR (echo Set VDARTS_GAME_DIR to the absolute path of the game folder. & exit /b 1)
set "GAME=%VDARTS_GAME_DIR%"
if "%GAME:~-1%"=="\" set "GAME=%GAME:~0,-1%"
set "ABS="
if "%GAME:~1,2%"==":\" set "ABS=1"
if "%GAME:~0,2%"=="\\" set "ABS=1"
if not defined ABS (echo VDARTS_GAME_DIR must be an absolute path, got "%GAME%". & exit /b 1)
if not exist "%GAME%\VDartsH2_V1_Data\Managed\Assembly-CSharp.dll" (echo VDARTS_GAME_DIR="%GAME%" is not the game folder. & exit /b 1)

set "VSDIR="
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
for /f "tokens=*" %%i in ('vswhere.exe -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath') do set "VSDIR=%%i"
if not defined VSDIR (echo MSVC not found & exit /b 1)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1

if not exist build mkdir build
cl /nologo /O2 /W4 /D_CRT_SECURE_NO_WARNINGS /MT /LD VShootingKernel.c /Fobuild\ /Fe:build\VShootingKernel.dll advapi32.lib || exit /b 1
cl /nologo /O2 /W3 /D_CRT_SECURE_NO_WARNINGS /MT test_vsk.c /Fobuild\ /Fe:build\test_vsk.exe || exit /b 1

dotnet publish BoardEmulator -c Release -o build\emulator --nologo -v q || exit /b 1
echo Board emulator: build\emulator\VDartsBoardEmulator.exe

rem This player does not search Data\Plugins; next to the exe is on the DLL search path.
copy /y build\VShootingKernel.dll "%GAME%\VShootingKernel.dll" >nul
echo Installed to %GAME%\VShootingKernel.dll

rem BepInEx plugin (needs BepInEx 5 x64 unpacked in the game folder).
if not exist "%GAME%\BepInEx\core\BepInEx.dll" (echo BepInEx not installed - skipping VDartsMod & exit /b 0)
dotnet build VDartsMod -c Release --nologo -v q || exit /b 1
if not exist "%GAME%\BepInEx\plugins" mkdir "%GAME%\BepInEx\plugins"
copy /y VDartsMod\bin\Release\net472\VDartsMod.dll "%GAME%\BepInEx\plugins\VDartsMod.dll" >nul
echo Installed to %GAME%\BepInEx\plugins\VDartsMod.dll
