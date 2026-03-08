@echo off
setlocal

set ROOT=%~dp0
set VC_VARS="D:\Visual Studio\2022\VC\Auxiliary\Build\vcvars64.bat"

if not exist %VC_VARS% (
  echo [FastGOAP] vcvars64.bat not found: %VC_VARS%
  exit /b 1
)

call %VC_VARS%
if errorlevel 1 exit /b %errorlevel%

if not exist "%ROOT%build\Release" mkdir "%ROOT%build\Release"

cl /nologo /std:c++17 /O2 /EHsc /DFASTGOAP_EXPORTS /LD "%ROOT%src\FastGoapMiddleware.cpp" /Fe:"%ROOT%build\Release\FastGoapMiddleware.dll"
if errorlevel 1 exit /b %errorlevel%

if not exist "%ROOT%..\..\Assets\Plugins\x86_64" mkdir "%ROOT%..\..\Assets\Plugins\x86_64"
copy /Y "%ROOT%build\Release\FastGoapMiddleware.dll" "%ROOT%..\..\Assets\Plugins\x86_64\FastGoapMiddleware.dll" >nul

echo [FastGOAP] Build and deploy succeeded.
exit /b 0
