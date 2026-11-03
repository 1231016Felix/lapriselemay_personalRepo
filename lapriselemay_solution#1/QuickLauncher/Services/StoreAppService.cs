using System.Runtime.InteropServices;
using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Abstraction pour le service de découverte des applications Store.
/// Permet l'injection de dépendances et la testabilité (Amélioration #2).
/// </summary>
public interface IStoreAppService
{
    List<SearchResult> GetAllApps();
    bool LaunchApp(string appUserModelId);
}

/// <summary>
/// Service pour découvrir les applications du Microsoft Store (UWP/MSIX)
/// via l'énumération du dossier virtuel shell:AppsFolder.
/// Converti de static vers injectable (Amélioration #2).
/// </summary>
public class StoreAppService : IStoreAppService
{
    #region COM Interop

    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem ppv);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IntPtr ppv);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList([MarshalAs(UnmanagedType.LPStruct)] Guid keyType, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("70629033-E363-4A28-A567-0DB78006E6D7")]
    private interface IEnumShellItems
    {
        void Next(uint celt, out IShellItem rgelt, out uint pceltFetched);
        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumShellItems ppenum);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, SHCONTF grfFlags, out IEnumIDList ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, ref uint rgfReserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, SHGDNF uFlags, out STRRET pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, SHGDNF uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F2-0000-0000-C000-000000000046")]
    private interface IEnumIDList
    {
        [PreserveSig]
        int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumIDList ppenum);
    }

    [Flags]
    private enum SHCONTF : uint
    {
        FOLDERS = 0x0020,
        NONFOLDERS = 0x0040,
        INCLUDEHIDDEN = 0x0080,
        INIT_ON_FIRST_NEXT = 0x0100,
        NETPRINTERSRCH = 0x0200,
        SHAREABLE = 0x0400,
        STORAGE = 0x0800,
        NAVIGATION_ENUM = 0x1000,
        FASTITEMS = 0x2000,
        FLATLIST = 0x4000,
        ENABLE_ASYNC = 0x8000
    }

    [Flags]
    private enum SHGDNF : uint
    {
        NORMAL = 0x0000,
        INFOLDER = 0x0001,
        FOREDITING = 0x1000,
        FORADDRESSBAR = 0x4000,
        FORPARSING = 0x8000
    }

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
        PARENTRELATIVEPARSING = 0x80018001,
        DESKTOPABSOLUTEPARSING = 0x80028000,
        PARENTRELATIVEEDITING = 0x80031001,
        DESKTOPABSOLUTEEDITING = 0x8004c000,
        FILESYSPATH = 0x80058000,
        URL = 0x80068000,
        PARENTRELATIVEFORADDRESSBAR = 0x8007c001,
        PARENTRELATIVE = 0x80080001,
        PARENTRELATIVEFORUI = 0x80094001
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STRRET
    {
        public uint uType;
        public IntPtr pOleStr;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToObject(
        IShellFolder psf,
        IntPtr pidl,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IntPtr ppv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrRetToBuf(ref STRRET pstr, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszBuf, uint cchBuf);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid BHID_EnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");
    private static readonly Guid BHID_SFObject = new("3981E224-F559-11D3-8E3A-00C04F6837D5");

    #endregion

    /// <summary>
    /// Énumère toutes les applications installées (traditionnelles et Store)
    /// </summary>
    public List<SearchResult> GetAllApps()
    {
        var allApps = new List<SearchResult>();

        try
        {
            // Obtenir le dossier AppsFolder via Shell
            if (SHParseDisplayName("shell:AppsFolder", IntPtr.Zero, out var pidlAppsFolder, 0, out _) != 0)
                return allApps;

            try
            {
                if (SHGetDesktopFolder(out var desktopFolder) != 0)
                    return allApps;

                try
                {
                    // Bind au dossier AppsFolder
                    if (SHBindToObject(desktopFolder, pidlAppsFolder, IntPtr.Zero, IID_IShellFolder, out var ppv) != 0)
                        return allApps;

                    var appsFolder = (IShellFolder)Marshal.GetObjectForIUnknown(ppv);

                    try
                    {
                        // Énumérer les applications
                        appsFolder.EnumObjects(IntPtr.Zero, SHCONTF.NONFOLDERS, out var enumIdList);

                        while (enumIdList.Next(1, out var pidl, out var fetched) == 0 && fetched == 1)
                        {
                            try
                            {
                                var app = GetAppFromPidl(appsFolder, pidl);
                                if (app != null)
                                    allApps.Add(app);
                            }
                            finally
                            {
                                CoTaskMemFree(pidl);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(appsFolder);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(desktopFolder);
                }
            }
            finally
            {
                CoTaskMemFree(pidlAppsFolder);
            }
        }
        catch
        {
            // Silently fail - on reviendra aux raccourcis traditionnels
        }

        // Dédupliquer par nom normalisé, en préférant les apps Store
        return allApps
            .GroupBy(app => NormalizeName(app.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(app => app.Type == ResultType.StoreApp ? 1 : 0) // Préférer StoreApp
                .ThenBy(app => app.Path.Length) // Sinon préférer le chemin le plus court
                .First())
            .ToList();
    }

    private static SearchResult? GetAppFromPidl(IShellFolder folder, IntPtr pidl)
    {
        try
        {
            // Obtenir le nom d'affichage
            folder.GetDisplayNameOf(pidl, SHGDNF.NORMAL, out var strretName);
            var nameBuffer = new System.Text.StringBuilder(260);
            StrRetToBuf(ref strretName, pidl, nameBuffer, (uint)nameBuffer.Capacity);
            var displayName = nameBuffer.ToString();

            // Obtenir l'AppUserModelId (pour le lancement)
            folder.GetDisplayNameOf(pidl, SHGDNF.FORPARSING, out var strretPath);
            var pathBuffer = new System.Text.StringBuilder(1024);
            StrRetToBuf(ref strretPath, pidl, pathBuffer, (uint)pathBuffer.Capacity);
            var appUserModelId = pathBuffer.ToString();

            // Ignorer les entrées vides ou système
            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(appUserModelId))
                return null;

            // Seules les apps avec '!' dans l'AppUserModelId sont de vraies apps UWP/Store
            // Ex: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" → StoreApp
            // Ex: "C:\Program Files\...\app.exe" ou "Microsoft.VisualStudio.Installer" → Application
            var isUwpApp = appUserModelId.Contains('!');

            return new SearchResult
            {
                Name = displayName,
                Path = appUserModelId,
                Description = isUwpApp ? "Microsoft Store" : "Application",
                Type = isUwpApp ? ResultType.StoreApp : ResultType.Application,
                IsFromAppsFolder = true
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Normalise un nom pour la comparaison (supprime espaces multiples, trim, etc.)
    /// </summary>
    private static string NormalizeName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        
        // Supprimer les espaces multiples et normaliser
        return System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s+", " ");
    }

    /// <summary>
    /// Lance une application via son AppUserModelId
    /// </summary>
    public bool LaunchApp(string appUserModelId)
    {
        try
        {
            // Utiliser explorer.exe shell:AppsFolder\{AppUserModelId}
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{appUserModelId}",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
