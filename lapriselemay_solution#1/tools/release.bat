@echo off
REM =====================================================================
REM  Outils de build QuickLauncher.
REM
REM  Deux usages :
REM    - En ligne de commande :  release.bat 1.1.1  [-DryRun]
REM                              release.bat test
REM    - Par double-clic       :  un menu propose les trois actions
REM
REM  La publication (et le DryRun) sont delegues a release.ps1 ; ce fichier
REM  n'est qu'un lanceur qui evite d'avoir a regler la politique
REM  d'execution PowerShell. Le test local, lui, se fait entierement ici :
REM  compilation Release puis lancement de l'executable produit.
REM
REM  Le test local compile en Release et non en Debug : c'est la config que
REM  les utilisateurs executent (optimisations activees, pas de symboles).
REM  Un build Debug donnerait une impression faussee des performances et des
REM  animations. Pour poser des points d'arret, utiliser Visual Studio (F5),
REM  qui est l'outil adapte a ca.
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
set "CONFIG=Release"
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
echo     1. Tester localement
echo        Compile en %CONFIG% et lance l'application.
echo        Ne touche ni au depot, ni a GitHub.
echo.
echo     2. Fabriquer l'installateur sans publier
echo        Build Release self-contained + packaging Velopack.
echo        Produit le Setup.exe en local, sans rien envoyer.
echo.
echo     3. Publier une nouvelle version sur GitHub
echo.
set "CHOIX="
set /p "CHOIX=  Choix (1/2/3) : "
if "%CHOIX%"=="1" goto :local
if "%CHOIX%"=="2" goto :dryrun
if "%CHOIX%"=="3" goto :demande_version
if "%CHOIX%"=="" goto :abandon
echo.
echo   Choix invalide.
goto :menu

REM =====================================================================
REM  1. TEST LOCAL
REM =====================================================================
:local
echo.
echo   ===================================================
echo     Test local - compilation %CONFIG%
echo   ===================================================
echo.

REM Une instance deja lancee depuis ce depot verrouille QuickLauncher.exe
REM et ferait echouer la compilation. On ne ferme QUE celle-la : la version
REM installee (dossier LocalAppData) ne contient pas "\bin\" et survit.
powershell.exe -NoProfile -Command "Get-Process QuickLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\bin\*' } | Stop-Process -Force"

dotnet build "%PROJ%" -c %CONFIG% --nologo -v minimal
if errorlevel 1 goto :echec_build

REM Le dossier de sortie contient le TFM (ex. net9.0-windows10.0.19041.0),
REM qu'on ne veut pas coder en dur. On prend le binaire pose DIRECTEMENT
REM dans bin\%CONFIG%\<tfm>\ : une recherche recursive pouvait remonter un
REM ancien dossier de publication (bin\%CONFIG%\<tfm>\win-x64) laisse par
REM une release, et donc lancer un binaire perime.
REM "for /d" ne parcourt que les sous-dossiers DIRECTS : il ne descendra
REM jamais dans win-x64. %%~fD normalise au passage le ".." du chemin.
set "EXE="
for /d %%D in ("%SLN%\QuickLauncher\bin\%CONFIG%\*") do if exist "%%~fD\QuickLauncher.exe" set "EXE=%%~fD\QuickLauncher.exe"

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
REM  2. INSTALLATEUR SANS PUBLIER  /  3. PUBLICATION
REM =====================================================================
:dryrun
set "EXTRA=-DryRun"
echo.
echo   ===================================================
echo     Installateur local (aucune publication)
echo   ===================================================
echo.
echo   Un numero de version est quand meme demande : il sert a
echo   nommer les paquets produits. Le depot n'est pas modifie,
echo   le bump est annule a la fin - tu pourras donc publier ce
echo   meme numero pour de vrai ensuite.
echo.
goto :saisie_version

:version_argument
set "VERSION=%ARG1%"
goto :run

:demande_version
echo.

:saisie_version
echo   Rappel : commit et push tes modifications avant de continuer,
echo   le script exige un working tree propre (meme sans publication).
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
echo   QuickLauncher.exe introuvable sous bin\%CONFIG%.
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
