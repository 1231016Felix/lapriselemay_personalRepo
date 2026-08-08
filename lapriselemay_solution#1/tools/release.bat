@echo off
REM =====================================================================
REM  Publie une nouvelle version de QuickLauncher sur les GitHub Releases.
REM
REM  Usage :
REM    release.bat 1.1.1              publie la version 1.1.1
REM    release.bat 1.1.1 -DryRun      build et package sans rien publier
REM
REM  La logique est dans release.ps1 ; ce fichier n'est qu'un lanceur qui
REM  evite d'avoir a regler la politique d'execution PowerShell.
REM =====================================================================

if "%~1"=="" (
    echo.
    echo   Usage : release.bat ^<version^> [-DryRun]
    echo   Exemple : release.bat 1.1.1
    echo.
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %*
exit /b %errorlevel%
