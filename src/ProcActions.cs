using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace SysMonApp
{
    // Actions on a process (by name, as shown in the dashboard): file location, end, web search
    static class ProcActions
    {
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        const uint QUERY_LIMITED = 0x1000;

        // Windows components that must not be ended from here
        static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services",
            "lsass", "lsaiso", "svchost", "dwm", "fontdrvhost", "explorer", "sihost", "ctfmon", "SecurityHealthService",
            "MsMpEng", "MsSense", "NisSrv", "SgrmBroker", "spoolsv", "WUDFHost", "SysMon"
        };

        public static Process[] Find(string name)
        {
            try { return Process.GetProcessesByName(name); } catch { return new Process[0]; }
        }

        // full exe paths of the running instances (works without admin for most processes)
        public static List<string> Paths(string name)
        {
            var paths = new List<string>();
            foreach (var p in Find(name))
            {
                IntPtr h = OpenProcess(QUERY_LIMITED, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new StringBuilder(1024); int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) paths.Add(sb.ToString());
                }
                finally { CloseHandle(h); }
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static void OpenLocation(string name, Window owner)
        {
            var paths = Paths(name);
            if (paths.Count == 0)
            {
                MessageBox.Show(owner, "Windows doesn't allow reading where \"" + name + "\" is installed without administrator rights (or it is no longer running).",
                                "SysMon", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start("explorer.exe", "/select,\"" + paths[0] + "\"");
        }

        public static void End(string name, Window owner)
        {
            if (Protected.Contains(name))
            {
                MessageBox.Show(owner, "\"" + name + "\" is part of Windows (or SysMon itself) and ending it could make the PC unstable, so SysMon won't do it.",
                                "SysMon", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var procs = Find(name);
            if (procs.Length == 0)
            {
                MessageBox.Show(owner, "\"" + name + "\" is not running any more.", "SysMon", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string what = procs.Length == 1 ? "the process \"" + name + "\"" : "all " + procs.Length + " processes named \"" + name + "\"";
            if (MessageBox.Show(owner, "End " + what + "?\n\nUnsaved work in it will be lost.", "SysMon - End process",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

            int ended = 0, denied = 0;
            foreach (var p in procs)
            {
                try { p.Kill(); ended++; }
                catch (InvalidOperationException) { ended++; }   // already exited
                catch { denied++; }
            }
            if (denied > 0)
                MessageBox.Show(owner, (ended > 0 ? "Ended " + ended + ", but " : "") + denied + " instance" + (denied == 1 ? "" : "s") +
                                " could not be ended. They probably belong to another user or need administrator rights (use Task Manager > Run as administrator).",
                                "SysMon", MessageBoxButton.OK, MessageBoxImage.Warning);
            else Program.Notify(3000, "SysMon", "Ended " + what + ".", System.Windows.Forms.ToolTipIcon.Info, null);
        }

        public static void Search(string name)
        {
            try { Process.Start("https://duckduckgo.com/?q=" + Uri.EscapeDataString(name + ".exe process")); } catch { }
        }

        public static string Describe(string name)
        {
            var procs = Find(name);
            var paths = Paths(name);
            return name + ": " + (procs.Length == 0 ? "not running" : procs.Length + " running instance" + (procs.Length == 1 ? "" : "s")) +
                   (paths.Count > 0 ? "\n" + paths[0] : "");
        }
    }
}
