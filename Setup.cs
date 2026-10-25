// BrightLock-Setup.exe: carries BrightLock.exe as an embedded resource and installs it
// for the current user (no admin): %LOCALAPPDATA%\Programs\BrightLock, a Start menu
// shortcut and an entry in Settings > Apps. Running it again updates in place.
// build.ps1 compiles it together with Install.cs and a generated version file.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BrightLock
{
    static class Setup
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            bool quiet = Array.IndexOf(args, "/S") >= 0 || Array.IndexOf(args, "--quiet") >= 0;
            string title = "BrightLock " + SetupVersion.Value + " Setup";

            if (!quiet)
            {
                string ask = Install.IsInstalled
                    ? "Update BrightLock to version " + SetupVersion.Value + "?\n\nYour settings are kept."
                    : "Install BrightLock " + SetupVersion.Value + "?\n\n" +
                      "It installs for your Windows account only (no admin rights), starts with Windows " +
                      "and lives in the taskbar tray. Uninstall any time from Settings > Apps.";
                if (MessageBox.Show(ask, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return 1;
            }

            try
            {
                Install.CloseRunning();
                Directory.CreateDirectory(Install.Dir);
                WriteExe(Install.ExePath);
                Install.WriteShortcut(Install.ExePath, Install.ShortcutPath);
                Install.Register(SetupVersion.Value);
            }
            catch (Exception e)
            {
                if (!quiet) MessageBox.Show("Setup could not finish:\n\n" + e.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            try { Process.Start(Install.ExePath); } catch { }
            if (!quiet)
                MessageBox.Show("BrightLock is installed and running.\n\nClick the sun icon in the taskbar tray " +
                    "(it may be under the ^ arrow) to set your monitors' brightness.", title,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        // The old exe can stay locked for a moment after it exits, so retry briefly.
        static void WriteExe(string path)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("BrightLock.exe"))
                    using (var dst = File.Create(path))
                        src.CopyTo(dst);
                    return;
                }
                catch (IOException)
                {
                    if (i >= 20) throw;
                    Thread.Sleep(250);
                }
            }
        }
    }
}
