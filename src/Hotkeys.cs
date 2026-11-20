using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace SysMonApp
{
    // System-wide shortcuts via RegisterHotKey on a hidden message-only window
    static class Hotkeys
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        const uint ALT = 1, CTRL = 2, SHIFT = 4, WIN = 8, NOREPEAT = 0x4000;
        static HwndSource src;
        static readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        public static readonly Dictionary<string, string> Status = new Dictionary<string, string>();   // name -> "" (ok) or error

        static void Init()
        {
            if (src != null) return;
            var p = new HwndSourceParameters("SysMonHotkeys") { WindowStyle = 0, ParentWindow = new IntPtr(-3) };   // HWND_MESSAGE
            src = new HwndSource(p);
            src.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                Action a;
                if (msg == 0x0312 && actions.TryGetValue(w.ToInt32(), out a)) { handled = true; try { a(); } catch (Exception ex) { Store.Log("hotkey: " + ex.Message); } }
                return IntPtr.Zero;
            });
        }

        public static void Apply(Settings s)
        {
            Init();
            foreach (var id in actions.Keys.ToList()) UnregisterHotKey(src.Handle, id);
            actions.Clear(); Status.Clear();
            if (!s.HotkeysOn) return;
            Register(1, "dashboard", s.HotkeyDashboard, ToggleDashboard);
            Register(2, "widget", s.HotkeyWidget, () => { s.WidgetVisible = !Program.W.IsVisible; Program.Apply(); });
            Register(3, "clickthrough", s.HotkeyClickThrough, () => { s.ClickThrough = !s.ClickThrough; Program.Apply(); });
        }

        // while a shortcut is being recorded in Settings, release ours so the key press reaches the box
        public static void Suspend()
        {
            if (src == null) return;
            foreach (var id in actions.Keys.ToList()) UnregisterHotKey(src.Handle, id);
            actions.Clear();
        }

        static void ToggleDashboard()
        {
            var d = Program.Dash;
            if (d != null && d.IsActive) d.Close();
            else Program.ShowDashboard();
        }

        static void Register(int id, string name, string text, Action a)
        {
            if (string.IsNullOrWhiteSpace(text)) { Status[name] = "off"; return; }
            uint mods, vk;
            if (!TryParse(text, out mods, out vk)) { Status[name] = "not a valid shortcut"; return; }
            if (RegisterHotKey(src.Handle, id, mods | NOREPEAT, vk)) { actions[id] = a; Status[name] = ""; }
            else Status[name] = "already used by another program";
        }

        public static bool TryParse(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            Key key = Key.None;
            foreach (var raw in text.Split('+'))
            {
                string p = raw.Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control") mods |= CTRL;
                else if (p == "alt") mods |= ALT;
                else if (p == "shift") mods |= SHIFT;
                else if (p == "win") mods |= WIN;
                else
                {
                    if (p.Length == 1 && char.IsDigit(p[0])) p = "d" + p;
                    if (!Enum.TryParse(p, true, out key)) return false;
                }
            }
            if (key == Key.None || mods == 0) return false;   // require at least one modifier
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return vk != 0;
        }

        // builds "Ctrl+Alt+M" from a key press in the settings window; null while only modifiers are held
        public static string FromKeyEvent(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin) return null;
            var m = Keyboard.Modifiers;
            var parts = new List<string>();
            if ((m & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((m & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((m & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((m & ModifierKeys.Windows) != 0) parts.Add("Win");
            if (parts.Count == 0) return null;
            string k = key.ToString();
            if (k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1])) k = k.Substring(1);
            parts.Add(k);
            return string.Join("+", parts);
        }
    }
}
