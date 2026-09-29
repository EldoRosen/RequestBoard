@echo off
setlocal enabledelayedexpansion
rem Usage: build.bat "C:\path\to\your\Torch folder"   (no trailing backslash)
set "TORCHDIR=%~1"
if "%TORCHDIR%"=="" set "TORCHDIR=C:\torch-server"

if not exist "%TORCHDIR%\Torch.dll" (
  echo Torch.dll not found in "%TORCHDIR%". Pass your Torch folder as the first argument.
  exit /b 1
)
if not exist "%TORCHDIR%\DedicatedServer64\Sandbox.Game.dll" (
  echo Game files not found in "%TORCHDIR%\DedicatedServer64". Start Torch once so it downloads the game, then retry.
  exit /b 1
)

rem protobuf-net.dll is required to build (used by the Nexus V3 code) - search rather than assume a fixed subfolder.
set "PROTOPATH="
for /f "delims=" %%F in ('dir /s /b "%TORCHDIR%\protobuf-net.dll" 2^>nul') do if not defined PROTOPATH set "PROTOPATH=%%F"
if not "%~2"=="" set "PROTOPATH=%~2"

if not defined PROTOPATH (
  echo protobuf-net.dll was not found anywhere under "%TORCHDIR%".
  echo Find it on your system (it ships with Space Engineers/Torch^) and re-run:
  echo   build.bat "%TORCHDIR%" "C:\full\path\to\protobuf-net.dll"
  exit /b 1
)
echo Found protobuf-net.dll at: %PROTOPATH%

dotnet build RequestBoard.csproj -c Release /p:TorchDir="%TORCHDIR%" /p:ProtoNetPath="%PROTOPATH%"
if errorlevel 1 (
  echo BUILD FAILED - see errors above.
  exit /b 1
)

if exist RequestBoard.zip del RequestBoard.zip
powershell -NoProfile -Command "Compress-Archive -Path 'bin\Release\net48\RequestBoard.dll','manifest.xml' -DestinationPath 'RequestBoard.zip' -Force"
echo.
echo Done. Copy RequestBoard.zip into your Torch Plugins folder.
