using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using WF = System.Windows.Forms;

namespace SysMonApp
{
    /*
      Update check against this project's GitHub releases (no account or token needed).
      Install: download SysMon.exe next to the running exe, verify it, then a tiny hidden cmd script waits for
      this process to exit, swaps the files and starts the new version.
    */
    static class Updater
    {
        const string Api = "https://api.github.com/repos/VPTaurus/sysmon/releases/latest";
        public const string ReleasesPage = "https://github.com/VPTaurus/sysmon/releases";

        public class Release { public string Version, Tag, Page, Url; public long Size; }

        public static Release Available;        // a newer release than the running one, if found
        public static string Status = "Not checked yet";
        public static event Action Changed;
        static bool busy;

        public static bool IsDevBuild { get { return Program.Version.Contains("-dev") || Program.Version == "dev"; } }

        static void Raise()
        {
            if (Changed != null && Program.W != null) Program.W.Dispatcher.BeginInvoke(new Action(() => { if (Changed != null) Changed(); }));
        }

        public static Release FetchLatest()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(Api);
            req.UserAgent = "SysMonWidget-updater"; req.Accept = "application/vnd.github+json"; req.Timeout = 15000;
            string json;
            using (var resp = req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = rd.ReadToEnd();
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var r = new Release { Tag = d["tag_name"] as string, Page = d["html_url"] as string };
            r.Version = (r.Tag ?? "").TrimStart('v', 'V');
            var assets = d.ContainsKey("assets") ? d["assets"] as IEnumerable : null;
            if (assets != null)
                foreach (var o in assets)
                {
                    var a = o as Dictionary<string, object>;
                    if (a == null || !string.Equals(a["name"] as string, "SysMon.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    r.Url = a["browser_download_url"] as string;
                    r.Size = Convert.ToInt64(a["size"]);
                }
            return r;
        }

        // semantic-version comparison: 1.2.0 > 1.2.0-dev+abc > 1.1.9
        public static int Compare(string a, string b)
        {
            Func<string, int[]> core = v =>
            {
                var p = (v ?? "0").TrimStart('v', 'V').Split('-', '+')[0].Split('.');
                var n = new int[3];
                for (int i = 0; i < 3 && i < p.Length; i++) int.TryParse(p[i], out n[i]);
                return n;
            };
            int[] x = core(a), y = core(b);
            for (int i = 0; i < 3; i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            bool preA = (a ?? "").Contains("-"), preB = (b ?? "").Contains("-");
            return preA == preB ? 0 : preA ? -1 : 1;
        }

        // manual = from Settings / tray: always reports the result; automatic: only notifies about new versions
        public static void CheckAsync(bool manual)
        {
            if (busy) return;
            busy = true;
            Status = "Checking...";
            Raise();
            Task.Factory.StartNew(() =>
            {
                try
                {
                    var r = FetchLatest();
                    bool newer = Compare(r.Version, Program.Version) > 0 && r.Url != null;
                    Available = newer ? r : null;
                    Status = newer ? "Version " + r.Version + " is available" : "Up to date (latest release is " + r.Version + ")";
                    Program.W.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Program.S.LastUpdateCheck = DateTime.Today.ToString("yyyy-MM-dd");
                        Program.SaveSettings();
                        if (newer && (manual || Program.S.SkipVersion != r.Version))
                            Program.Notify(10000, "SysMon " + r.Version + " is available", "You have " + Program.Version + ". Click to update now (SysMon restarts).",
                                           WF.ToolTipIcon.Info, () => Program.OfferUpdate());
                        else if (manual) Program.Notify(4000, "SysMon", Status + ".", WF.ToolTipIcon.Info, null);
                    }));
                }
                catch (Exception ex) { Status = "Could not check: " + ex.Message; Store.Log("update check: " + ex.Message); }
                finally { busy = false; Raise(); }
            });
        }

        // downloads and verifies the new exe, then starts the swap script and exits the app
        public static void Install(Release r, Action exitApp)
        {
            string exe = Assembly.GetEntryAssembly().Location;
            string dir = Path.GetDirectoryName(exe);
            string tmp = Path.Combine(dir, "SysMon.update.exe");
            Status = "Downloading " + r.Version + "...";
            Raise();
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "SysMonWidget-updater";
                wc.DownloadFile(r.Url, tmp);
            }
            var bytes = File.ReadAllBytes(tmp);
            if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z' || (r.Size > 0 && bytes.Length != r.Size))
            {
                File.Delete(tmp);
                throw new InvalidDataException("the downloaded file is not a valid SysMon.exe");
            }
            int pid = Process.GetCurrentProcess().Id;
            string script = Path.Combine(Path.GetTempPath(), "sysmon-update-" + pid + ".cmd");
            File.WriteAllText(script,
                "@echo off\r\n" +
                ":wait\r\n" +
                "tasklist /FI \"PID eq " + pid + "\" 2>nul | find \"" + pid + "\" >nul\r\n" +
                "if not errorlevel 1 ( ping -n 2 127.0.0.1 >nul & goto wait )\r\n" +
                "move /y \"" + tmp + "\" \"" + exe + "\" >nul\r\n" +
                "start \"\" \"" + exe + "\"\r\n" +
                "del \"%~f0\"\r\n", Encoding.ASCII);
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            exitApp();
        }
    }
}
