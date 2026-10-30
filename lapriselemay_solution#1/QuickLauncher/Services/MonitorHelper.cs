using System.Runtime.InteropServices;
using System.Windows;

namespace QuickLauncher.Services;

/// <summary>
/// Géométrie multi-écran en coordonnées WPF (DIP). <see cref="SystemParameters.WorkArea"/> ne
/// décrit que l'écran PRINCIPAL : une position sauvegardée sur un écran secondaire (y compris
/// à gauche ou au-dessus du principal, donc en coordonnées négatives) était rejetée.
/// </summary>
public static class MonitorHelper
{
    /// <summary>
    /// Zones de travail (hors barre des tâches) de tous les écrans, chacune convertie en DIP
    /// avec le DPI effectif de son propre écran.
    /// </summary>
    public static IReadOnlyList<Rect> GetWorkAreas()
    {
        var areas = new List<Rect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (TryGetWorkArea(monitor, out var area))
                areas.Add(area);
            return true;
        }, IntPtr.Zero);
        return areas;
    }

    /// <summary>Zone de travail (DIP) de l'écran sous le pointeur de la souris, ou null.</summary>
    public static Rect? GetWorkAreaUnderCursor()
    {
        if (!GetCursorPos(out var point)) return null;
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        return TryGetWorkArea(monitor, out var area) ? area : null;
    }

    /// <summary>La fenêtre tient-elle entièrement dans la zone de travail d'un des écrans ?</summary>
    public static bool FitsInAnyWorkArea(double left, double top, double width, double height, IEnumerable<Rect> workAreas)
        => workAreas.Any(a => left >= a.Left && left + width <= a.Right && top >= a.Top && top + height <= a.Bottom);

    /// <summary>Zone de travail contenant le point (DIP), ou null.</summary>
    public static Rect? FindWorkAreaContaining(double x, double y, IEnumerable<Rect> workAreas)
    {
        foreach (var area in workAreas)
        {
            if (x >= area.Left && x < area.Right && y >= area.Top && y < area.Bottom)
                return area;
        }
        return null;
    }

    private static bool TryGetWorkArea(IntPtr monitor, out Rect area)
    {
        area = default;
        if (monitor == IntPtr.Zero) return false;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var scale = 1.0;
        if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;

        var work = info.Work;
        area = new Rect(work.Left / scale, work.Top / scale,
            (work.Right - work.Left) / scale, (work.Bottom - work.Top) / scale);
        return true;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
