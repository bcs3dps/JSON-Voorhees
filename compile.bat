@echo off
setlocal
REM =============================================================================
REM compile.bat -- Build script for Voorhees
REM
REM SPDX-License-Identifier: GPL-2.0-or-later
REM Copyright (c) 2026 B. C. Services
REM =============================================================================
REM Compiles Voorhees into a single Voorhees.exe using the C# compiler that
REM ships with the .NET Framework 4.x (csc.exe).  No Visual Studio, MSBuild or
REM NuGet is needed.
REM
REM Usage (from any directory; every path is relative to this file's folder,
REM and Voorhees.exe is written into this same folder):
REM   compile.bat            release build (the default)
REM   compile.bat RELEASE    release build
REM   compile.bat DEBUG      debug build (defines DEBUG, writes Voorhees.pdb)
REM
REM The version is fixed in src\Version.cs; this script does not change it.
REM
REM Source files.  Every file compiled is named below -- there is no wildcard.
REM A listed file that is missing fails the build, and a file that is not
REM listed is not part of the program:
REM   src\lib\BusyModalForm.cs   -- the progress dialog
REM   src\Version.cs             -- the version string
REM   src\VoorheesDocument.cs    -- the document model, changes, undo, search
REM   src\VoorheesText.cs        -- file bytes to text and back (every format)
REM   src\VoorheesJson.cs        -- the lossless JSON reader and writer
REM   src\VoorheesLines.cs       -- the line engine (INI, CFG, REG)
REM   src\VoorheesIni.cs         -- the INI rules
REM   src\VoorheesCfg.cs         -- the Klipper / Moonraker / KlipperScreen rules
REM   src\VoorheesReg.cs         -- the registry file (.reg) rules
REM   src\VoorheesFormat.cs      -- the format dispatcher
REM   src\VoorheesTreeView.cs    -- the tree control
REM   src\Voorhees.cs            -- the program and its windows
REM and the icon, res\Voorhees.ico (the program's icon, and read by the About
REM box).
REM
REM References:
REM   System.dll                 -- core types, collections, IO
REM   System.Drawing.dll         -- GDI+ graphics (Font, Color, Size)
REM   System.Windows.Forms.dll   -- the WinForms UI framework
REM =============================================================================

echo.
echo ============================================
echo  Voorhees Build
echo ============================================
echo.

REM Work in this file's folder, whatever the current directory is.
cd /d "%~dp0"

REM Build flags: release (optimized) unless DEBUG is asked for.
set BUILD_FLAGS=/optimize+ /debug-
set BUILD_KIND=RELEASE
if /i "%~1"=="DEBUG" (
    set BUILD_FLAGS=/define:DEBUG /debug:full /optimize-
    set BUILD_KIND=DEBUG
)
echo  *** %BUILD_KIND% BUILD ***
echo.

REM Close a running Voorhees, so its exe can be replaced.
taskkill /f /im Voorhees.exe >nul 2>&1

REM Find csc.exe in the standard .NET Framework 4.x locations.
set CSC=
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" (
    set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
) else if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" (
    set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) else (
    echo ERROR: Could not find csc.exe.
    echo Make sure the .NET Framework 4.x is installed.
    exit /b 1
)
echo Using compiler: %CSC%
echo.

REM Compile into Voorhees.exe.  The exit code is captured on the very next
REM line: any command run in between could reset ERRORLEVEL and let a failed
REM compile report success.
"%CSC%" ^
    /target:winexe ^
    /out:Voorhees.exe ^
    /win32icon:res\Voorhees.ico ^
    /resource:res\Voorhees.ico,Voorhees.ico ^
    /reference:System.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    /warn:4 ^
    /nologo ^
    %BUILD_FLAGS% ^
    src\lib\BusyModalForm.cs ^
    src\Version.cs ^
    src\VoorheesDocument.cs ^
    src\VoorheesText.cs ^
    src\VoorheesJson.cs ^
    src\VoorheesLines.cs ^
    src\VoorheesIni.cs ^
    src\VoorheesCfg.cs ^
    src\VoorheesReg.cs ^
    src\VoorheesFormat.cs ^
    src\VoorheesTreeView.cs ^
    src\Voorhees.cs
set CSC_ERR=%ERRORLEVEL%

if "%CSC_ERR%"=="0" (
    echo.
    echo ============================================
    echo  BUILD SUCCESSFUL
    echo  Output: %CD%\Voorhees.exe
    echo ============================================
) else (
    echo.
    echo ============================================
    echo  BUILD FAILED
    echo  Check the errors above and fix them.
    echo ============================================
)

echo.
exit /b %CSC_ERR%
