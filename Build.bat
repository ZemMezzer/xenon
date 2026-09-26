@echo off
setlocal

for %%I in ("%~dp0.") do set "ROOT=%%~fI"
set "XENON_PLATFORM_ID=%~1"

if "%XENON_PLATFORM_ID%"=="" (
  if /I "%PROCESSOR_ARCHITEW6432%"=="ARM64" (
    set "XENON_PLATFORM_ID=win_arm64"
  ) else if /I "%PROCESSOR_ARCHITECTURE%"=="ARM64" (
    set "XENON_PLATFORM_ID=win_arm64"
  ) else if /I "%PROCESSOR_ARCHITEW6432%"=="AMD64" (
    set "XENON_PLATFORM_ID=win_x64"
  ) else if /I "%PROCESSOR_ARCHITECTURE%"=="AMD64" (
    set "XENON_PLATFORM_ID=win_x64"
  ) else if /I "%PROCESSOR_ARCHITECTURE%"=="x86" (
    set "XENON_PLATFORM_ID=win_x86"
  ) else (
    echo Unsupported Windows host architecture: %PROCESSOR_ARCHITECTURE%
    exit /b 1
  )
) else (
  if not "%~2"=="" (
    echo Build.bat accepts one platform argument; set XENON_VERSION for release builds.
    exit /b 1
  )
)

if /I "%XENON_PLATFORM_ID%"=="win_arm64" (
  set "CMAKE_ARCH=ARM64"
) else if /I "%XENON_PLATFORM_ID%"=="win_x64" (
  set "CMAKE_ARCH=x64"
) else if /I "%XENON_PLATFORM_ID%"=="win_x86" (
  set "CMAKE_ARCH=Win32"
) else (
  echo Unsupported Xenon build platform: %XENON_PLATFORM_ID%. Supported: win_x64, win_arm64, win_x86.
  exit /b 1
)

set "VERSION_ARG="
if defined XENON_VERSION set "VERSION_ARG=-DXENON_VERSION=%XENON_VERSION%"

cmake -S "%ROOT%" -B "%ROOT%\build\%XENON_PLATFORM_ID%\cmake" ^
  -G "Visual Studio 17 2022" -A "%CMAKE_ARCH%" ^
  -DXENON_PLATFORM=%XENON_PLATFORM_ID% %VERSION_ARG%
if errorlevel 1 exit /b %errorlevel%

cmake --build "%ROOT%\build\%XENON_PLATFORM_ID%\cmake" --config Release --target xenon-native
exit /b %errorlevel%
