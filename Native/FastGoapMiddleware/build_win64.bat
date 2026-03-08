@echo off
setlocal

set "ROOT=%~dp0"
set "VC_VARS="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "BUILD_DIR=%ROOT%build\Release"
set "OUT_DLL=%BUILD_DIR%\FastGoapMiddleware.dll"
set "UNITY_PLUGIN_DIR=%ROOT%..\..\Assets\Plugins\x86_64"

REM 先尝试常见 VS 安装路径
if exist "D:\Visual Studio\2022\VC\Auxiliary\Build\vcvars64.bat" set "VC_VARS=D:\Visual Studio\2022\VC\Auxiliary\Build\vcvars64.bat"
if not defined VC_VARS if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VC_VARS=%ProgramFiles%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if not defined VC_VARS if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VC_VARS=%ProgramFiles%\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"
if not defined VC_VARS if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat" set "VC_VARS=%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"

REM 再尝试通过 vswhere 自动发现最新安装
if not defined VC_VARS if exist "%VSWHERE%" (
  for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do (
    if exist "%%I\VC\Auxiliary\Build\vcvars64.bat" set "VC_VARS=%%I\VC\Auxiliary\Build\vcvars64.bat"
  )
)

if not defined VC_VARS (
  echo [FastGOAP] vcvars64.bat not found.
  echo [FastGOAP] Install Visual Studio C++ workload, or edit this script with your vcvars64.bat path.
  exit /b 1
)

echo [FastGOAP] Using toolchain: "%VC_VARS%"
call "%VC_VARS%"
if errorlevel 1 exit /b %errorlevel%

where cl >nul 2>nul
if errorlevel 1 (
  echo [FastGOAP] cl.exe not found after vcvars initialization.
  exit /b 1
)

if not exist "%BUILD_DIR%" mkdir "%BUILD_DIR%"

cl /nologo /std:c++20 /utf-8 /O2 /EHsc /DFASTGOAP_EXPORTS /LD ^
  "%ROOT%src\FastGoapMiddleware.cpp" ^
  "%ROOT%src\FastGoapPlanner.cpp" ^
  "%ROOT%src\FastGoapRuntime.cpp" ^
  /Fe:"%OUT_DLL%"
if errorlevel 1 exit /b %errorlevel%

if not exist "%UNITY_PLUGIN_DIR%" mkdir "%UNITY_PLUGIN_DIR%"
copy /Y "%OUT_DLL%" "%UNITY_PLUGIN_DIR%\FastGoapMiddleware.dll" >nul
if errorlevel 1 (
  echo [FastGOAP] Build succeeded, but deploy copy failed.
  echo [FastGOAP] DLL: "%OUT_DLL%"
  exit /b 0
)

echo [FastGOAP] Build and deploy succeeded.
echo [FastGOAP] DLL: "%OUT_DLL%"
exit /b 0
