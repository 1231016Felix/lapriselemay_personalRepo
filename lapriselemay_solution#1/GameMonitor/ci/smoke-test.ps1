# Test de fumée de GameMonitor sur un vrai Windows (utilisé par le CI, droits administrateur requis) :
# démarrage, session ETW des FPS, placement dans les quatre coins de l'écran, instance unique,
# pause quand l'overlay est masqué, fermeture propre sans session ETW laissée ouverte.
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
[DllImport("user32.dll")]
public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")]
static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
[DllImport("user32.dll")]
static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
[StructLayout(LayoutKind.Sequential)]
struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
// Rectangle de l'écran qui contient la fenêtre (MONITOR_DEFAULTTONEAREST).
public static RECT ScreenOf(IntPtr hwnd) {
    var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
    GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref info);
    return info.Monitor;
}
'@

$WM_CLOSE = 0x0010
$WM_HOTKEY = 0x0312
$HK_DETAILS = 1
$HK_TOGGLE = 2
$HK_CORNER = 3
# Bureau virtuel (tous les écrans) : origine, puis largeur et hauteur.
$SM_XVIRTUALSCREEN = 76
$SM_YVIRTUALSCREEN = 77
$SM_CXVIRTUALSCREEN = 78
$SM_CYVIRTUALSCREEN = 79

function Get-Overlay([int] $processId) {
    $hwnd = [IntPtr]::Zero
    # [NullString]::Value et non $null : PowerShell passerait "" (titre vide) à la place de NULL.
    while (($hwnd = [Smoke.Win32]::FindWindowExW([IntPtr]::Zero, $hwnd, 'GameMonitorOverlay', [NullString]::Value)) -ne [IntPtr]::Zero) {
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

function Save-Screen([string] $name, [int] $processId) {
    if (-not $Screenshot) { return }
    try {
        Add-Type -AssemblyName System.Drawing
        # Tous les écrans : l'overlay s'affiche sur celui du premier plan, pas forcément le principal.
        $originX = [Smoke.Win32]::GetSystemMetrics($SM_XVIRTUALSCREEN)
        $originY = [Smoke.Win32]::GetSystemMetrics($SM_YVIRTUALSCREEN)
        $bmp = [System.Drawing.Bitmap]::new(
            [Smoke.Win32]::GetSystemMetrics($SM_CXVIRTUALSCREEN), [Smoke.Win32]::GetSystemMetrics($SM_CYVIRTUALSCREEN))
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($originX, $originY, 0, 0, $bmp.Size)
        $g.Dispose()
        $null = New-Item -ItemType Directory -Force -Path $Screenshot
        $bmp.Save((Join-Path $Screenshot "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)

        # L'overlay seul, aussi dans le journal en base64 (visible sans télécharger l'artefact).
        $hwnd = Get-Overlay $processId
        $rect = [Smoke.Win32+RECT]::new()
        if ($hwnd -ne [IntPtr]::Zero -and [Smoke.Win32]::GetWindowRect($hwnd, [ref] $rect)) {
            $area = [System.Drawing.Rectangle]::Intersect(
                [System.Drawing.Rectangle]::FromLTRB(
                    $rect.Left - $originX, $rect.Top - $originY, $rect.Right - $originX, $rect.Bottom - $originY),
                [System.Drawing.Rectangle]::new(0, 0, $bmp.Width, $bmp.Height))
            $crop = $bmp.Clone($area, $bmp.PixelFormat)
            $stream = [System.IO.MemoryStream]::new()
            $crop.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $crop.Dispose()
            $base64 = [Convert]::ToBase64String($stream.ToArray())
            Write-Host "::group::Capture $name ($($area.Width) x $($area.Height) px, PNG en base64)"
            for ($i = 0; $i -lt $base64.Length; $i += 4000) {
                Write-Host $base64.Substring($i, [Math]::Min(4000, $base64.Length - $i))
            }
            Write-Host '::endgroup::'
        }
        $bmp.Dispose()
    } catch {
        Write-Host "::warning::Capture d'écran impossible : $_"
    }
}

# Dans le coin demandé (0 haut gauche, 1 haut droit, 2 bas droit, 3 bas gauche), l'overlay doit tenir
# entier sur son écran, à la marge près des deux bords (10 px à 100 %, plus sur un écran mis à l'échelle).
function Test-Corner([int] $processId, [int] $corner) {
    $hwnd = Get-Overlay $processId
    $rect = [Smoke.Win32+RECT]::new()
    if ($hwnd -eq [IntPtr]::Zero -or -not [Smoke.Win32]::GetWindowRect($hwnd, [ref] $rect)) {
        Fail "coin $corner : fenêtre de l'overlay introuvable"
        return
    }
    $screen = [Smoke.Win32]::ScreenOf($hwnd)
    $inside = ($rect.Left -ge $screen.Left -and $rect.Top -ge $screen.Top -and
        $rect.Right -le $screen.Right -and $rect.Bottom -le $screen.Bottom)
    $dx = if ($corner -in 1, 2) { $screen.Right - $rect.Right } else { $rect.Left - $screen.Left }
    $dy = if ($corner -in 2, 3) { $screen.Bottom - $rect.Bottom } else { $rect.Top - $screen.Top }
    if (-not $inside -or $dx -gt 64 -or $dy -gt 64) {
        Fail ("coin {0} : overlay en ({1}, {2}, {3}, {4}), hors du coin de l'écran ({5}, {6}, {7}, {8})" -f $corner,
            $rect.Left, $rect.Top, $rect.Right, $rect.Bottom, $screen.Left, $screen.Top, $screen.Right, $screen.Bottom)
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
    Save-Screen 'overlay-compact' $first.Id

    $null = Send-Overlay $first.Id $WM_HOTKEY $HK_DETAILS
    Start-Sleep -Milliseconds 1500
    Save-Screen 'overlay-details' $first.Id
    $null = Send-Overlay $first.Id $WM_HOTKEY $HK_DETAILS

    # 2. Placement : Ctrl+Maj+F11 fait le tour des quatre coins de l'écran
    foreach ($corner in 0..3) {
        Start-Sleep -Milliseconds 500
        Test-Corner $first.Id $corner
        $null = Send-Overlay $first.Id $WM_HOTKEY $HK_CORNER
    }

    # 3. Un second lancement ne doit pas créer une seconde instance
    $second = Start-Overlay $path
    if (-not $second.WaitForExit(10000)) {
        Fail 'un second lancement reste ouvert à côté du premier (pas d''instance unique)'
    }
    if ($first.HasExited) { Fail 'le premier GameMonitor s''est arrêté lors du second lancement' }
    if (-not (Test-EtwSession)) { Fail 'session ETW perdue après le second lancement' }

    # 4. Masqué : la collecte, ETW compris, est en pause
    $null = Send-Overlay $first.Id $WM_HOTKEY $HK_TOGGLE
    Start-Sleep -Seconds 3
    if (Test-EtwSession) { Fail 'session ETW toujours active quand l''overlay est masqué' }
    $null = Send-Overlay $first.Id $WM_HOTKEY $HK_TOGGLE
    Start-Sleep -Seconds 3
    if (-not (Test-EtwSession)) { Fail 'session ETW non relancée quand l''overlay réapparaît' }

    # 5. Fermeture propre (comme Ctrl+Maj+F12) : plus aucune session ETW ensuite
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
