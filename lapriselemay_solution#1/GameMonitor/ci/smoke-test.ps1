# Test de fumée de GameMonitor sur un vrai Windows (utilisé par le CI, droits administrateur requis) :
# démarrage, session ETW des FPS, instance unique, pause quand l'overlay est masqué,
# fermeture propre sans session ETW laissée ouverte.
#
#   pwsh -File ci/smoke-test.ps1 -Exe target/release/GameMonitor.exe [-Screenshot dossier]
#
# Attention : ferme les GameMonitor déjà lancés.

param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Screenshot
)

$ErrorActionPreference = 'Stop'
$Session = 'GameMonitorFPS'
$failures = [System.Collections.Generic.List[string]]::new()

function Fail([string] $message) {
    Write-Host "::error::$message"
    $failures.Add($message)
}

Add-Type -Namespace Smoke -Name Win32 -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
[DllImport("user32.dll")]
public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")]
public static extern int GetSystemMetrics(int index);
'@

$WM_CLOSE = 0x0010
$WM_HOTKEY = 0x0312
$HK_DETAILS = 1
$HK_TOGGLE = 2

function Get-Overlay([int] $processId) {
    $hwnd = [IntPtr]::Zero
    while (($hwnd = [Smoke.Win32]::FindWindowExW([IntPtr]::Zero, $hwnd, 'GameMonitorOverlay', $null)) -ne [IntPtr]::Zero) {
        [uint32] $owner = 0
        [void] [Smoke.Win32]::GetWindowThreadProcessId($hwnd, [ref] $owner)
        if ($owner -eq $processId) { return $hwnd }
    }
    return [IntPtr]::Zero
}

# Même effet que les raccourcis Ctrl+Maj+F9 à F12 : le programme reçoit WM_HOTKEY.
function Send-Overlay([int] $processId, [uint32] $message, [int] $wParam = 0) {
    $hwnd = Get-Overlay $processId
    if ($hwnd -eq [IntPtr]::Zero) { return $false }
    return [Smoke.Win32]::PostMessageW($hwnd, $message, [IntPtr] $wParam, [IntPtr]::Zero)
}

function Test-EtwSession {
    $null = logman query $Session -ets 2>&1
    return $LASTEXITCODE -eq 0
}

function Save-Screen([string] $name) {
    if (-not $Screenshot) { return }
    try {
        Add-Type -AssemblyName System.Drawing
        $bmp = [System.Drawing.Bitmap]::new([Smoke.Win32]::GetSystemMetrics(0), [Smoke.Win32]::GetSystemMetrics(1))
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
        $null = New-Item -ItemType Directory -Force -Path $Screenshot
        $bmp.Save((Join-Path $Screenshot "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose()
        $bmp.Dispose()
    } catch {
        Write-Host "::warning::Capture d'écran impossible : $_"
    }
}

function Stop-Overlay($process, [string] $label) {
    if ($null -eq $process -or $process.HasExited) { return }
    if (-not (Send-Overlay $process.Id $WM_CLOSE)) {
        Fail "$label : fenêtre introuvable pour la fermer proprement"
    } elseif (-not $process.WaitForExit(10000)) {
        Fail "$label : toujours lancé 10 s après WM_CLOSE"
    } elseif ($process.ExitCode -ne 0) {
        Fail "$label : code de sortie $($process.ExitCode)"
    }
}

function Start-Overlay([string] $path) {
    $process = Start-Process -FilePath $path -PassThru
    $null = $process.Handle # garde le handle pour pouvoir lire le code de sortie
    return $process
}

function Invoke-Scenario([string] $path) {
    # 1. Démarrage
    $first = Start-Overlay $path
    Start-Sleep -Seconds 4
    if ($first.HasExited) {
        Fail "GameMonitor s'est arrêté au démarrage (code $($first.ExitCode))"
        return
    }
    if ((Get-Overlay $first.Id) -eq [IntPtr]::Zero) { Fail 'fenêtre de l''overlay introuvable' }
    if (-not (Test-EtwSession)) { Fail "session ETW « $Session » absente pendant l'exécution" }
    Save-Screen 'overlay-compact'

    $null = Send-Overlay $first.Id $WM_HOTKEY $HK_DETAILS
    Start-Sleep -Milliseconds 1500
    Save-Screen 'overlay-details'
    $null = Send-Overlay $first.Id $WM_HOTKEY $HK_DETAILS

    # 2. Un second lancement ne doit pas créer une seconde instance
    $second = Start-Overlay $path
    if (-not $second.WaitForExit(10000)) {
        Fail 'un second lancement reste ouvert à côté du premier (pas d''instance unique)'
    }
    if ($first.HasExited) { Fail 'le premier GameMonitor s''est arrêté lors du second lancement' }
    if (-not (Test-EtwSession)) { Fail 'session ETW perdue après le second lancement' }

    # 3. Masqué : la collecte, ETW compris, est en pause
    $null = Send-Overlay $first.Id $WM_HOTKEY $HK_TOGGLE
    Start-Sleep -Seconds 3
    if (Test-EtwSession) { Fail 'session ETW toujours active quand l''overlay est masqué' }
    $null = Send-Overlay $first.Id $WM_HOTKEY $HK_TOGGLE
    Start-Sleep -Seconds 3
    if (-not (Test-EtwSession)) { Fail 'session ETW non relancée quand l''overlay réapparaît' }

    # 4. Fermeture propre (comme Ctrl+Maj+F12) : plus aucune session ETW ensuite
    Stop-Overlay $first 'GameMonitor'
    Stop-Overlay $second 'Second lancement'
    if (Test-EtwSession) { Fail "session ETW « $Session » laissée ouverte après la fermeture" }
}

$exePath = (Resolve-Path $Exe).Path
$admin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "Exe : $exePath (administrateur : $admin)"

Get-Process GameMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
$null = logman stop $Session -ets 2>&1
try {
    Invoke-Scenario $exePath
} finally {
    Get-Process GameMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
    $null = logman stop $Session -ets 2>&1
}

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) vérification(s) en échec."
    exit 1
}
Write-Host 'Test de fumée réussi.'
exit 0
