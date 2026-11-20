using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace SysMonApp
{
    /*
      Per-user install (no admin): %LOCALAPPDATA%\Programs\SysMon Widget\SysMon.exe, a Start Menu shortcut and an
      entry in Settings > Apps (HKCU ...\Uninstall\SysMonWidget). The uninstaller is the installed exe itself.
    */
    static class Installer
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SysMon Widget");
        public static string InstalledExe { get { return Path.Combine(Dir, "SysMon.exe"); } }
        static string Shortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SysMon Widget.lnk"); } }
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SysMonWidget";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static string Me { get { return Assembly.GetEntryAssembly().Location; } }
        public static bool IsInstalledCopy { get { return string.Equals(Path.GetFullPath(Me), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); } }
        public static bool IsInstalled { get { return File.Exists(InstalledExe); } }

        // registers / refreshes the Apps entry and Start Menu shortcut (called by the installed copy at every start)
        public static void Register()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", "SysMon Widget");
                    k.SetValue("DisplayVersion", Program.Version);
                    k.SetValue("Publisher", "VPTaurus");
                    k.SetValue("DisplayIcon", InstalledExe + ",0");
                    k.SetValue("InstallLocation", Dir);
                    k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                    k.SetValue("QuietUninstallString", "\"" + InstalledExe + "\" --uninstall --quiet");
                    k.SetValue("URLInfoAbout", "https://github.com/VPTaurus/sysmon");
                    k.SetValue("HelpLink", "https://github.com/VPTaurus/sysmon");
                    k.SetValue("URLUpdateInfo", Updater.ReleasesPage);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    if (File.Exists(InstalledExe)) k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                    if (k.GetValue("InstallDate") == null) k.SetValue("InstallDate", DateTime.Today.ToString("yyyyMMdd"));
                }
                if (!File.Exists(Shortcut)) CreateShortcut(Shortcut, InstalledExe);
            }
            catch (Exception ex) { Store.Log("register: " + ex.Message); }
        }

        static void CreateShortcut(string lnkPath, string target)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnkPath });
            var lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Always-on-top system resource and network monitor" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }

        static void StopOthers(bool onlyInstalled)
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("SysMon"))
            {
                if (p.Id == me) continue;
                try
                {
                    if (onlyInstalled && !string.Equals(p.MainModule.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill(); p.WaitForExit(5000);
                }
                catch { }
            }
        }

        // copies this exe into the install folder and registers it; start = launch the installed copy afterwards
        public static void Install(bool start)
        {
            StopOthers(true);
            Directory.CreateDirectory(Dir);
            if (!IsInstalledCopy) File.Copy(Me, InstalledExe, true);
            Register();
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))      // keep "start with Windows" pointing at the installed copy
                if (k != null && k.GetValue("SysMonWidget") != null) k.SetValue("SysMonWidget", "\"" + InstalledExe + "\"");
            if (start) Process.Start(InstalledExe);
        }

        // asks once when a non-installed release build is started (e.g. downloaded from GitHub)
        public static bool OfferInstall()
        {
            string msg = IsInstalled
                ? "SysMon is already installed, but you started a different copy (" + Program.Version + ").\n\nReplace the installed SysMon with this version?"
                : "Install SysMon for your user?\n\nIt is copied to your Programs folder and gets a Start Menu entry and an entry in Settings > Apps " +
                  "(so it can be uninstalled there). No administrator rights needed.\n\nYes = install and start it,  No = just run this copy.";
            if (MessageBox.Show(msg, "SysMon", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            try { Install(true); return true; }
            catch (Exception ex)
            {
                MessageBox.Show("Installation failed: " + ex.Message, "SysMon", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // returns false when the user cancelled
        public static bool Uninstall(bool quiet)
        {
            bool deleteData = false;
            if (!quiet)
            {
                if (MessageBox.Show("Remove SysMon from this PC?", "Uninstall SysMon", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
                long size = 0;
                try { size = Directory.Exists(Store.Dir) ? Directory.GetFiles(Store.Dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0; } catch { }
                deleteData = MessageBox.Show("Also delete your SysMon settings, logs and history (" + Fmt.Size(size) + ")?\n\nNo = keep them, in case you install SysMon again.",
                                             "Uninstall SysMon", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
            }
            StopOthers(false);
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null && k.GetValue("SysMonWidget") != null) k.DeleteValue("SysMonWidget"); } catch { }
            try { if (File.Exists(Shortcut)) File.Delete(Shortcut); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            if (deleteData) try { Directory.Delete(Store.Dir, true); } catch { }

            // the program folder (possibly containing this running exe) is removed after we exit
            if (Directory.Exists(Dir))
            {
                int pid = Process.GetCurrentProcess().Id;
                string script = Path.Combine(Path.GetTempPath(), "sysmon-uninstall-" + pid + ".cmd");
                File.WriteAllText(script,
                    "@echo off\r\n:wait\r\n" +
                    "tasklist /FI \"PID eq " + pid + "\" 2>nul | find \"" + pid + "\" >nul\r\n" +
                    "if not errorlevel 1 ( ping -n 2 127.0.0.1 >nul & goto wait )\r\n" +
                    "rmdir /s /q \"" + Dir + "\"\r\n" +
                    "del \"%~f0\"\r\n", Encoding.ASCII);
                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            if (!quiet)
                MessageBox.Show("SysMon was removed." + (deleteData ? "" : "\nYour settings and logs were kept in " + Store.Dir + "."), "Uninstall SysMon",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
    }
}
