@echo off
REM =====================================================================
REM  Outils de build QuickLauncher.
REM
REM  Deux usages :
REM    - En ligne de commande :  release.bat 1.1.1  [-DryRun]
REM                              release.bat test
REM    - Par double-clic       :  un menu propose tester ou publier
REM
REM  La publication est deleguee a release.ps1 ; ce fichier n'est qu'un
REM  lanceur qui evite d'avoir a regler la politique d'execution
REM  PowerShell. Le test local, lui, se fait entierement ici : compilation
REM  Debug puis lancement de l'executable produit.
REM
REM  Pas d'accents dans les messages : la console cmd les afficherait mal.
REM
REM  Les sauts vers des etiquettes (goto) sont volontaires : dans un bloc
REM  entre parentheses, cmd developpe les variables au moment de l'analyse,
REM  donc une valeur saisie par set /p ne serait pas visible dans le meme
REM  bloc. Avec goto, chaque instruction est evaluee au moment ou on y passe.
REM =====================================================================

setlocal

set "ARG1=%~1"
set "EXTRA=%~2"
set "INTERACTIVE="
set "CODE=0"
set "SLN=%~dp0.."
set "PROJ=%SLN%\QuickLauncher\QuickLauncher.csproj"

REM ---------------------------------------------------------------------
REM  Mode ligne de commande
REM ---------------------------------------------------------------------
if /i "%ARG1%"=="test"   goto :local
if /i "%ARG1%"=="-test"  goto :local
if /i "%ARG1%"=="--test" goto :local
if not "%ARG1%"=="" goto :version_argument

REM ---------------------------------------------------------------------
REM  Mode interactif (double-clic)
REM ---------------------------------------------------------------------
set "INTERACTIVE=1"

:menu
echo.
echo   ===================================================
echo     QuickLauncher - outils de build
echo   ===================================================
echo.
echo     1. Tester localement  (compile en Debug puis lance)
echo     2. Publier une nouvelle version sur GitHub
echo.
set "CHOIX="
set /p "CHOIX=  Choix (1/2) : "
if "%CHOIX%"=="1" goto :local
if "%CHOIX%"=="2" goto :demande_version
if "%CHOIX%"=="" goto :abandon
echo.
echo   Choix invalide.
goto :menu

REM =====================================================================
REM  TEST LOCAL
REM =====================================================================
:local
echo.
echo   ===================================================
echo     Test local - compilation Debug
echo   ===================================================
echo.

REM Une instance deja lancee depuis ce dossier verrouille QuickLauncher.exe
REM et ferait echouer la compilation. On ne ferme QUE celle-la : la version
REM installee (dossier LocalAppData) n'est pas touchee.
powershell.exe -NoProfile -Command "Get-Process QuickLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\bin\Debug\*' } | Stop-Process -Force"

dotnet build "%PROJ%" -c Debug --nologo -v minimal
if errorlevel 1 goto :echec_build

REM Le dossier de sortie contient le TFM (net9.0-windows10.0.x) : on le
REM cherche plutot que de le coder en dur, pour survivre a une montee de
REM version du framework.
set "EXE="
for /f "delims=" %%F in ('dir /b /s "%SLN%\QuickLauncher\bin\Debug\QuickLauncher.exe" 2^>nul') do set "EXE=%%F"
if not defined EXE goto :exe_introuvable

echo.
echo   Lancement : %EXE%
echo.
echo   NOTE : si la version installee tourne encore, les deux se disputent
echo          le raccourci global. Quitte-la via l'icone de la zone de
echo          notification avant de tester.
echo.
start "" "%EXE%"
goto :fin

REM =====================================================================
REM  PUBLICATION
REM =====================================================================
:version_argument
set "VERSION=%ARG1%"
goto :run

:demande_version
echo.
echo   Rappel : commit et push tes modifications avant de
echo   publier, le script exige un working tree propre.
echo.
set "VERSION="
set /p "VERSION=  Numero de version (ex. 1.1.1) : "
if "%VERSION%"=="" goto :abandon
echo.

:run
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %VERSION% %EXTRA%
set "CODE=%errorlevel%"
goto :fin

REM =====================================================================
REM  SORTIES
REM =====================================================================
:echec_build
echo.
echo   La compilation a echoue, rien n'a ete lance.
set "CODE=1"
goto :fin

:exe_introuvable
echo.
echo   QuickLauncher.exe introuvable sous bin\Debug.
set "CODE=1"
goto :fin

:abandon
echo.
echo   Aucune saisie, abandon.
set "CODE=1"

:fin
REM La fenetre d'un double-clic se refermerait avant d'avoir pu lire quoi
REM que ce soit : on attend une touche. Inutile depuis un terminal.
if defined INTERACTIVE echo.
if defined INTERACTIVE pause

exit /b %CODE%
