@echo off
setlocal enabledelayedexpansion
rem Builds DesktopBuckets.ShellExt.dll (x64) -> %~dp0out\
rem If cl.exe is already on PATH (e.g. a VS Developer prompt, or the
rem ilammy/msvc-dev-cmd CI action), it is used directly; otherwise this
rem searches for and calls vcvars64.bat.

set "HERE=%~dp0"
set "OUT=%HERE%out"
if not exist "%OUT%" mkdir "%OUT%"

where cl.exe >nul 2>nul
if %errorlevel%==0 goto :compile

set "VCVARS="
for %%p in ("%ProgramFiles%" "%ProgramFiles(x86)%") do (
  for %%e in (Community Professional Enterprise BuildTools Preview) do (
    for /d %%v in ("%%~p\Microsoft Visual Studio\*") do (
      if exist "%%~v\%%e\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS=%%~v\%%e\VC\Auxiliary\Build\vcvars64.bat"
    )
  )
)
if not defined VCVARS (
  echo ERROR: cl.exe not on PATH and no vcvars64.bat found.
  exit /b 1
)
echo Using !VCVARS!
call "!VCVARS!" >nul || exit /b 1

:compile
pushd "%OUT%"
cl /nologo /c /std:c++17 /EHsc /W3 /O2 /MT /GS /DUNICODE /D_UNICODE "%HERE%dllmain.cpp" || (popd & exit /b 1)
link /nologo /DLL /OUT:"%OUT%\DesktopBuckets.ShellExt.dll" /DEF:"%HERE%ShellExt.def" dllmain.obj Shlwapi.lib Shell32.lib Ole32.lib OleAut32.lib User32.lib RuntimeObject.lib || (popd & exit /b 1)
popd

echo Built: %OUT%\DesktopBuckets.ShellExt.dll
exit /b 0
