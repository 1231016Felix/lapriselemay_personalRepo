<#
.SYNOPSIS
    Vérifie que release.ps1 ne divulgue jamais de jeton dans ses messages d'erreur.

.DESCRIPTION
    Charge uniquement les fonctions Format-SafeArguments et Invoke-Checked de release.ps1
    (via l'AST : le script de release n'est pas exécuté), fait échouer une commande locale
    à laquelle on passe un faux jeton, puis vérifie que ni la ligne formatée ni le message
    de l'exception ne contiennent ce jeton. Aucun identifiant réel n'est lu.

.EXAMPLE
    .\tools\Test-ReleaseSecrets.ps1
#>
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'release.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$null)
$functions = $ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -in 'Format-SafeArguments', 'Invoke-Checked'
    }, $true)

if (@($functions).Count -ne 2) {
    throw 'Fonctions Format-SafeArguments et Invoke-Checked introuvables dans release.ps1'
}
foreach ($function in $functions) {
    . ([scriptblock]::Create($function.Extent.Text))
}

$fakeToken = 'FAUX_JETON_' + [guid]::NewGuid().ToString('N')
$failures = 0

# 1. Formatage : formes « --token valeur » et « --token=valeur »
$formatted = Format-SafeArguments @('upload', 'github', '--token', $fakeToken, "--token=$fakeToken", '--tag', 'v1')
if ($formatted -match [regex]::Escape($fakeToken)) {
    Write-Host "ÉCHEC : Format-SafeArguments laisse passer le jeton : $formatted" -ForegroundColor Red
    $failures++
}
if ($formatted -notmatch '--tag v1') {
    Write-Host "ÉCHEC : les arguments non sensibles doivent rester lisibles : $formatted" -ForegroundColor Red
    $failures++
}

# 2. Commande qui échoue réellement : le message d'erreur ne doit pas contenir le jeton
try {
    Invoke-Checked -Exe 'cmd.exe' -ErrorMessage 'Échec attendu' -Arguments @('/c', 'exit', '3', '--token', $fakeToken) | Out-Null
    Write-Host 'ÉCHEC : la commande de test aurait dû échouer' -ForegroundColor Red
    $failures++
}
catch {
    if ($_.Exception.Message -match [regex]::Escape($fakeToken)) {
        Write-Host "ÉCHEC : le jeton apparaît dans le message d'erreur : $($_.Exception.Message)" -ForegroundColor Red
        $failures++
    }
}

if ($failures -gt 0) {
    exit 1
}

Write-Host 'OK : aucun jeton divulgué dans les messages de release.ps1' -ForegroundColor Green
