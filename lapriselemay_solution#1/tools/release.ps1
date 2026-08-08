<#
.SYNOPSIS
    Publie une nouvelle version de QuickLauncher sur les GitHub Releases.

.DESCRIPTION
    Enchaîne les étapes d'une release Velopack :
      1. Vérifications préalables (outils, branche, working tree propre)
      2. Bump de la version dans QuickLauncher.csproj et Constants.cs
      3. Publish self-contained win-x64
      4. Récupération de la release précédente (permet les mises à jour delta)
      5. Packaging Velopack (installateur + portable + nupkg)
      6. Commit et push du bump de version
      7. Upload et publication de la GitHub Release

    En cas d'échec après le bump, les fichiers modifiés sont automatiquement
    restaurés pour ne jamais laisser le dépôt dans un état incohérent.

.PARAMETER Version
    Numéro de version au format X.Y.Z. Doit être supérieur à la version actuelle.

.PARAMETER DryRun
    Build et package localement, mais ne touche ni à git ni à GitHub.
    À utiliser pour valider une release avant de la rendre publique.

.EXAMPLE
    .\release.ps1 1.1.1

.EXAMPLE
    .\release.ps1 1.2.0 -DryRun
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

# =====================================================================
#  Configuration
# =====================================================================

$SolutionDir = Split-Path -Parent $PSScriptRoot
$ProjectDir  = Join-Path $SolutionDir 'QuickLauncher'
$Csproj      = Join-Path $ProjectDir 'QuickLauncher.csproj'
$ConstantsCs = Join-Path $ProjectDir 'Constants.cs'
$IconPath    = Join-Path $ProjectDir 'Resources\app.ico'

# ATTENTION : ne jamais changer PackId. Velopack s'en sert pour identifier
# l'application installée ; le modifier casserait la chaîne de mise à jour
# de tous les utilisateurs existants.
$PackId      = 'LapriseLemay.QuickLauncher'
$PackTitle   = 'QuickLauncher'
$PackAuthors = 'Felix-Antoine Laprise-Lemay'
$MainExe     = 'QuickLauncher.exe'
$Rid         = 'win-x64'
$Shortcuts   = 'StartMenu,Desktop'

$RepoUrl     = 'https://github.com/1231016Felix/lapriselemay_personalRepo'
$Branch      = 'main'
$TagPrefix   = 'quicklauncher-v'

$BuildRoot   = Join-Path $env:TEMP 'quicklauncher-release'
$PublishDir  = Join-Path $BuildRoot 'publish'
$ReleasesDir = Join-Path $BuildRoot 'releases'

# =====================================================================
#  Utilitaires
# =====================================================================

$script:StepNumber = 0

function Write-Step {
    param([string] $Message)
    $script:StepNumber++
    Write-Host ''
    Write-Host "[$script:StepNumber] $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string] $Message)
    Write-Host "    OK  $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string] $Message)
    Write-Host "    !   $Message" -ForegroundColor Yellow
}

function Invoke-Checked {
    <#
        Exécute une commande externe et lève une exception si son code de
        sortie n'est pas 0. Indispensable : PowerShell ignore par défaut les
        échecs des exécutables natifs, ce qui laisserait le script continuer
        joyeusement après un build raté.
    #>
    param(
        [Parameter(Mandatory)] [string]   $Exe,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [string] $ErrorMessage = 'Commande échouée'
    )

    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$ErrorMessage (code $LASTEXITCODE) : $Exe $($Arguments -join ' ')"
    }
}

function Get-GitHubToken {
    <#
        Récupère le jeton GitHub depuis Git Credential Manager, celui-là même
        qui sert déjà à 'git push'. Aucun jeton n'est stocké dans ce script.
    #>
    $answer = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null

    if ($LASTEXITCODE -ne 0 -or -not $answer) {
        throw "Impossible de lire les identifiants GitHub. Fais un 'git push' une fois pour les enregistrer."
    }

    $line = $answer | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if (-not $line) {
        throw 'Aucun jeton trouvé dans Git Credential Manager pour github.com.'
    }

    return $line.Substring('password='.Length)
}

function Update-VersionInFile {
    <#
        Remplace la version dans un fichier en préservant la présence ou
        l'absence de BOM UTF-8, pour éviter de polluer le diff git.
        Lève une exception si le motif ne correspond à rien : un remplacement
        silencieusement raté produirait une release au mauvais numéro.
    #>
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Pattern,
        [Parameter(Mandatory)] [string] $Replacement
    )

    $bytes   = [System.IO.File]::ReadAllBytes($Path)
    $hasBom  = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $content = [System.IO.File]::ReadAllText($Path)
    $updated = [regex]::Replace($content, $Pattern, $Replacement)

    if ($updated -eq $content) {
        throw "Motif introuvable dans $(Split-Path -Leaf $Path) : $Pattern"
    }

    [System.IO.File]::WriteAllText($Path, $updated, (New-Object System.Text.UTF8Encoding($hasBom)))
}

# =====================================================================
#  1. Vérifications préalables
# =====================================================================

$Tag           = "$TagPrefix$Version"
$versionBumped = $false

Write-Host ''
Write-Host "  Release $PackTitle $Version" -ForegroundColor White
if ($DryRun) { Write-Warn 'Mode DryRun : aucune modification git ni GitHub' }

Write-Step 'Vérifications préalables'

foreach ($tool in 'dotnet', 'git', 'vpk') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        if ($tool -eq 'vpk') {
            throw "vpk introuvable. Installe-le avec : dotnet tool install -g vpk --version 1.2.0"
        }
        throw "$tool introuvable dans le PATH."
    }
}
Write-Ok 'dotnet, git et vpk présents'

foreach ($file in $Csproj, $ConstantsCs, $IconPath) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Fichier attendu introuvable : $file"
    }
}
Write-Ok 'Fichiers du projet trouvés'

Push-Location $SolutionDir
try {
    $currentBranch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($currentBranch -ne $Branch) {
        throw "Tu es sur la branche '$currentBranch', la release doit partir de '$Branch'."
    }

    if (git status --porcelain) {
        throw "Le working tree contient des modifications non commitées. Commit ou stash avant de publier."
    }
    Write-Ok "Branche $Branch, working tree propre"

    # La version actuelle sert de garde-fou : republier un numéro déjà sorti
    # ferait échouer la mise à jour côté clients (Velopack compare les versions).
    $csprojContent  = [System.IO.File]::ReadAllText($Csproj)
    $currentVersion = [regex]::Match($csprojContent, '<Version>([^<]+)</Version>').Groups[1].Value

    if (-not $currentVersion) {
        throw "Impossible de lire <Version> dans QuickLauncher.csproj."
    }
    if ([version]$Version -le [version]$currentVersion) {
        throw "La version $Version n'est pas supérieure à la version actuelle ($currentVersion)."
    }
    Write-Ok "Version $currentVersion -> $Version"

    $existingTag = git ls-remote --tags origin "refs/tags/$Tag"
    if ($existingTag) {
        throw "Le tag $Tag existe déjà sur origin. Supprime-le ou choisis une autre version."
    }
    Write-Ok "Tag $Tag disponible"

    # =================================================================
    #  2. Bump de version
    # =================================================================

    Write-Step 'Mise à jour du numéro de version'

    Update-VersionInFile -Path $Csproj `
        -Pattern '<Version>[^<]+</Version>' `
        -Replacement "<Version>$Version</Version>"

    Update-VersionInFile -Path $ConstantsCs `
        -Pattern 'public const string Version = "[^"]+";' `
        -Replacement "public const string Version = ""$Version"";"

    $versionBumped = $true
    Write-Ok 'QuickLauncher.csproj et Constants.cs mis à jour'

    # =================================================================
    #  3. Publish
    # =================================================================

    Write-Step 'Compilation self-contained'

    # QuickLauncher verrouille ses propres binaires quand il tourne : la copie
    # de l'apphost échouerait en plein build.
    $running = Get-Process -Name 'QuickLauncher' -ErrorAction SilentlyContinue
    if ($running) {
        $running | Stop-Process -Force
        Start-Sleep -Seconds 2
        Write-Warn 'QuickLauncher était en cours d''exécution, il a été fermé'
    }

    if (Test-Path -LiteralPath $BuildRoot) {
        Remove-Item -LiteralPath $BuildRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $ReleasesDir -Force | Out-Null

    Invoke-Checked -Exe 'dotnet' -ErrorMessage 'Le publish a échoué' -Arguments @(
        'publish', $Csproj,
        '-c', 'Release',
        '-r', $Rid,
        '--self-contained',
        '-o', $PublishDir,
        '-v', 'quiet',
        '-nologo'
    )

    $publishedExe = Join-Path $PublishDir $MainExe
    if (-not (Test-Path -LiteralPath $publishedExe)) {
        throw "Le publish n'a pas produit $MainExe."
    }

    $sizeMb = [math]::Round((Get-ChildItem $PublishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Ok "Publish terminé ($sizeMb Mo)"

    # =================================================================
    #  4. Release précédente (pour les mises à jour delta)
    # =================================================================

    Write-Step 'Récupération de la release précédente'

    $token = $null
    if (-not $DryRun) {
        $token = Get-GitHubToken

        # Best-effort : sans la version précédente en local, Velopack produit
        # un paquet complet au lieu d'un delta. La release reste valide, elle
        # est juste plus lourde à télécharger pour les utilisateurs.
        try {
            Invoke-Checked -Exe 'vpk' -ErrorMessage 'Download échoué' -Arguments @(
                'download', 'github',
                '--outputDir', $ReleasesDir,
                '--repoUrl', $RepoUrl,
                '--token', $token
            )
            Write-Ok 'Release précédente récupérée, les deltas seront générés'
        }
        catch {
            Write-Warn "Release précédente indisponible : paquet complet uniquement ($($_.Exception.Message))"
        }
    }
    else {
        Write-Warn 'Ignoré en mode DryRun'
    }

    # =================================================================
    #  5. Packaging Velopack
    # =================================================================

    Write-Step 'Packaging Velopack'

    Invoke-Checked -Exe 'vpk' -ErrorMessage 'Le packaging a échoué' -Arguments @(
        'pack',
        '--packId', $PackId,
        '--packVersion', $Version,
        '--packDir', $PublishDir,
        '--mainExe', $MainExe,
        '--packTitle', $PackTitle,
        '--packAuthors', $PackAuthors,
        '--icon', $IconPath,
        '--shortcuts', $Shortcuts,
        '--outputDir', $ReleasesDir
    )

    $setupExe = Join-Path $ReleasesDir "$PackId-win-Setup.exe"
    if (-not (Test-Path -LiteralPath $setupExe)) {
        throw "L'installateur n'a pas été généré : $setupExe"
    }

    $setupMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
    Write-Ok "Installateur généré ($setupMb Mo)"

    if ($DryRun) {
        Write-Host ''
        Write-Host "  DryRun terminé. Paquets dans : $ReleasesDir" -ForegroundColor Yellow
        Write-Warn 'Les fichiers de version restent modifiés localement (non commités)'
        return
    }

    # =================================================================
    #  6. Commit et push
    # =================================================================

    Write-Step 'Commit et push du bump de version'

    Invoke-Checked -Exe 'git' -Arguments @('add', $Csproj, $ConstantsCs) `
        -ErrorMessage 'git add a échoué'

    Invoke-Checked -Exe 'git' -Arguments @('commit', '-m', "$PackTitle v$Version") `
        -ErrorMessage 'git commit a échoué'

    # Le push précède l'upload : la release GitHub référence le commit via
    # --targetCommitish, il doit donc déjà exister sur origin.
    Invoke-Checked -Exe 'git' -Arguments @('push', 'origin', $Branch) `
        -ErrorMessage 'git push a échoué'

    Write-Ok "Commit poussé sur origin/$Branch"

    # =================================================================
    #  7. Publication de la release
    # =================================================================

    Write-Step 'Publication de la GitHub Release'

    Invoke-Checked -Exe 'vpk' -ErrorMessage "L'upload a échoué" -Arguments @(
        'upload', 'github',
        '--outputDir', $ReleasesDir,
        '--repoUrl', $RepoUrl,
        '--token', $token,
        '--tag', $Tag,
        '--releaseName', "$PackTitle v$Version",
        '--targetCommitish', $Branch,
        '--publish', 'true'
    )

    # Le tag est créé côté GitHub par vpk : on le rapatrie pour que le dépôt
    # local reflète l'état réel de origin.
    git fetch origin --tags --quiet

    Write-Host ''
    Write-Host "  Release publiée : $RepoUrl/releases/tag/$Tag" -ForegroundColor Green
    Write-Host '  Les installations existantes recevront la mise à jour automatiquement.' -ForegroundColor Green
    Write-Host ''
}
catch {
    # Le bump de version ne doit jamais survivre à un échec : sinon le dépôt
    # annonce une version qui n'a jamais été publiée.
    if ($versionBumped) {
        git checkout -- $Csproj $ConstantsCs 2>$null
        Write-Warn 'Modifications de version annulées'
    }

    Write-Host ''
    Write-Host "  ECHEC : $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    exit 1
}
finally {
    Pop-Location
}
