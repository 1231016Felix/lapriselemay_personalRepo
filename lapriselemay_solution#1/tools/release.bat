@echo off
REM =====================================================================
REM  Publie une nouvelle version de QuickLauncher sur les GitHub Releases.
REM
REM  Deux usages :
REM    - En ligne de commande :  release.bat 1.1.1  [-DryRun]
REM    - Par double-clic       :  le script demande la version
REM
REM  La logique est dans release.ps1 ; ce fichier n'est qu'un lanceur qui
REM  evite d'avoir a regler la politique d'execution PowerShell.
REM
REM  Les sauts vers des etiquettes (goto) sont volontaires : dans un bloc
REM  entre parentheses, cmd developpe les variables au moment de l'analyse,
REM  donc une valeur saisie par set /p ne serait pas visible dans le meme
REM  bloc. Avec goto, chaque instruction est evaluee au moment ou on y passe.
REM =====================================================================

setlocal

set "VERSION=%~1"
set "EXTRA=%~2"
set "INTERACTIVE="

REM Version fournie en argument : on lance directement.
if not "%VERSION%"=="" goto :run

REM Sinon, mode interactif (double-clic).
set "INTERACTIVE=1"
echo.
echo   ===================================================
echo     Publication d'une nouvelle version QuickLauncher
echo   ===================================================
echo.
echo   Rappel : commit et push tes modifications avant de
echo   publier, le script exige un working tree propre.
echo.

set /p "VERSION=  Numero de version (ex. 1.1.1) : "
if "%VERSION%"=="" goto :aucune

set /p "DRY=  Test sans rien publier ? (o/N) : "
REM On ne teste que la premiere lettre : accepte o, O, oui, Oui, yes...
if /i "%DRY:~0,1%"=="o" set "EXTRA=-DryRun"
if /i "%DRY:~0,1%"=="y" set "EXTRA=-DryRun"
echo.

:run
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %VERSION% %EXTRA%
set "CODE=%errorlevel%"

REM La fenetre d'un double-clic se refermerait avant d'avoir pu lire quoi
REM que ce soit : on attend une touche. Inutile depuis un terminal.
if defined INTERACTIVE echo.
if defined INTERACTIVE pause

exit /b %CODE%

:aucune
echo.
echo   Aucune version saisie, abandon.
echo.
pause
exit /b 1
