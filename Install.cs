// Install, update and uninstall for BrightLock. Compiled into both BrightLock.exe
// (for --uninstall) and BrightLock-Setup.exe. Per-user only, so no admin rights.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BrightLock
{
    static class Install
    {
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BrightLock";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\BrightLock"); }
        }
        public static string ExePath { get { return Path.Combine(Dir, "BrightLock.exe"); } }
        public static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "BrightLock.lnk"); }
        }
        public static string SettingsDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrightLock"); }
        }

        public static bool IsInstalled
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey)) return k != null; }
        }

        // Asks a running BrightLock to exit (it restores the screen colours on the way out);
        // kills it only if it doesn't respond, e.g. an old version without the exit signal.
        public static void CloseRunning()
        {
            int self = Process.GetCurrentProcess().Id;
            try { using (var ev = EventWaitHandle.OpenExisting("BrightLock.Exit")) ev.Set(); } catch { }
            foreach (var p in Process.GetProcessesByName("BrightLock"))
            {
                try
                {
                    if (p.Id == self) continue;
                    if (!p.WaitForExit(4000)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        public static void WriteShortcut(string target, string path)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                var t = lnk.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Monitor brightness over DDC/CI" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        // Entry in Settings > Apps > Installed apps.
        public static void Register(string version)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "BrightLock");
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", "AntonRylin");
                k.SetValue("DisplayIcon", "\"" + ExePath + "\"");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + ExePath + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + ExePath + "\" --uninstall --quiet");
                k.SetValue("URLInfoAbout", "https://github.com/AntonRylin/brightlock");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                try { k.SetValue("EstimatedSize", (int)(new FileInfo(ExePath).Length / 1024), RegistryValueKind.DWord); } catch { }
            }
        }
    }

    static class Uninstaller
    {
        public static void Run(bool quiet)
        {
            if (!quiet && MessageBox.Show("Remove BrightLock and its settings from this PC?", "Uninstall BrightLock",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            Install.CloseRunning();
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) if (k != null) k.DeleteValue("BrightLock", false); } catch { }
            try { File.Delete(Install.ShortcutPath); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(Install.UninstallKey, false); } catch { }
            try { Directory.Delete(Install.SettingsDir, true); } catch { }

            // This exe is inside the install folder, so a hidden cmd removes the folders once it has
            // exited (the settings folder again too, in case a file in it was still briefly in use).
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Install.Dir + "\" & rmdir /s /q \"" + Install.SettingsDir + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
            }
            catch { }

            if (!quiet) MessageBox.Show("BrightLock was removed.", "Uninstall BrightLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
