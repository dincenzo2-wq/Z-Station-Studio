using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ZStationStudio.Services
{
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLinkCom {}

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    public static class DesktopShortcutHelper
    {
        public static void EnsureDesktopShortcut()
        {
            try
            {
                string? currentExe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
                {
                    currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                }

                if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe)) return;

                string ownerDropboxExe = @"F:\Business Dropbox\Tran Vinh\Tài Nguyên Dựng Phim\DaVinci Resolve Studio\Z-Station_Studio\Z-Station_Studio.exe";
                if (DataManager.IsStandaloneInstall && File.Exists(ownerDropboxExe))
                {
                    // Running standalone test on the owner's machine: preserve owner's personal Dropbox shortcut
                    return;
                }

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnk = Path.Combine(desktop, "Z-Station Studio.lnk");
                string[] oldLnks = new[] {
                    Path.Combine(desktop, "HNH SFX Finder.lnk"),
                    Path.Combine(desktop, "Z Station Studio.lnk"),
                    Path.Combine(desktop, "z-station.lnk")
                };
                foreach (var old in oldLnks)
                {
                    if (File.Exists(old))
                    {
                        try { File.Delete(old); } catch { }
                    }
                }

                var link = (IShellLinkW)new ShellLinkCom();
                link.SetPath(currentExe);
                link.SetWorkingDirectory(Path.GetDirectoryName(currentExe) ?? "");
                link.SetDescription("Z-Station Studio");
                link.SetIconLocation(currentExe, 0);

                var persist = (IPersistFile)link;
                persist.Save(lnk, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Shortcut creation error: {ex.Message}");
            }
        }
    }
}
