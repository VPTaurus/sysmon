<#
  SysMon Widget - always-on-top system resource + network traffic monitor.
  Pure PowerShell 5.1 + WPF, no dependencies. Locale-independent (no perf counters).

  Run:        powershell -NoProfile -ExecutionPolicy Bypass -STA -File SysMon.ps1
  Shortcut:   powershell -NoProfile -ExecutionPolicy Bypass -File SysMon.ps1 -InstallShortcut
              (creates "SysMon Widget.lnk" next to this script that starts it without a console)

  Drag to move. Right-click for options. Tray icon: double-click to show/hide.
#>
param([switch]$InstallShortcut)

$ErrorActionPreference = 'Stop'
$ScriptPath = $MyInvocation.MyCommand.Path
$AppDir = Join-Path $env:APPDATA 'SysMonWidget'
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
$LogFile      = Join-Path $AppDir 'error.log'
$SettingsFile = Join-Path $AppDir 'settings.json'
$StartupLnk   = Join-Path ([Environment]::GetFolderPath('Startup')) 'SysMon Widget.lnk'

function Write-Log($msg) { try { "$(Get-Date -Format s)  $msg" | Add-Content -Path $LogFile } catch {} }

function New-Launcher([string]$lnkPath) {
    $sh = New-Object -ComObject WScript.Shell
    $l = $sh.CreateShortcut($lnkPath)
    $l.TargetPath = Join-Path $env:WINDIR 'System32\conhost.exe'
    $l.Arguments = "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File `"$ScriptPath`""
    $l.WorkingDirectory = Split-Path $ScriptPath
    $l.IconLocation = (Join-Path $env:WINDIR 'System32\perfmon.exe') + ',0'
    $l.Description = 'SysMon Widget'
    $l.Save()
}

if ($InstallShortcut) {
    $p = Join-Path (Split-Path $ScriptPath) 'SysMon Widget.lnk'
    New-Launcher $p
    "Created $p"
    return
}

# ---------- single instance ----------
$created = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\SysMonWidget_v1', [ref]$created)
if (-not $created) { return }

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing

# ---------- native helpers ----------
Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

public static class SysMon {
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
    [StructLayout(LayoutKind.Sequential)]
    class MEMORYSTATUSEX {
        public uint dwLength = 64; public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    public static ulong UptimeMs() { return GetTickCount64(); }

    // ---- CPU ----
    static long pIdle, pKernel, pUser;
    public static double Cpu() {
        long i, k, u;
        if (!GetSystemTimes(out i, out k, out u)) return 0;
        long di = i - pIdle, dk = k - pKernel, du = u - pUser;
        bool first = pKernel == 0;
        pIdle = i; pKernel = k; pUser = u;
        long total = dk + du;              // kernel time includes idle
        if (first || total <= 0) return 0;
        return Math.Max(0, Math.Min(100, (total - di) * 100.0 / total));
    }

    // ---- RAM ----
    public static double MemLoad, MemTotalGB, MemUsedGB;
    public static void Mem() {
        var m = new MEMORYSTATUSEX();
        if (!GlobalMemoryStatusEx(m)) return;
        MemTotalGB = m.ullTotalPhys / 1073741824.0;
        MemUsedGB  = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        MemLoad    = MemTotalGB > 0 ? MemUsedGB / MemTotalGB * 100 : 0;
    }

    // ---- Network ----
    static Dictionary<string, long[]> prev = new Dictionary<string, long[]>();
    static DateTime prevT = DateTime.MinValue;
    public static double RxRate, TxRate;
    public static long RxTotal, TxTotal;
    public static string IfName = "";

    static bool HasGateway(NetworkInterface n) {
        try {
            foreach (var g in n.GetIPProperties().GatewayAddresses) {
                var a = g.Address;
                if (!a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)) return true;
            }
        } catch { }
        return false;
    }

    static long Delta(long now, long before) {
        long d = now - before;
        if (d < 0 && before <= 0xFFFFFFFFL) d += 0x100000000L;   // 32-bit counter wrap
        return d < 0 ? 0 : d;
    }

    public static void ResetTotals() { RxTotal = 0; TxTotal = 0; }

    public static void Net() {
        DateTime now = DateTime.UtcNow;
        double dt = prevT == DateTime.MinValue ? 0 : (now - prevT).TotalSeconds;
        prevT = now;

        var up = new List<NetworkInterface>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces()) {
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            var t = n.NetworkInterfaceType;
            if (t == NetworkInterfaceType.Loopback || t == NetworkInterfaceType.Tunnel) continue;
            up.Add(n);
        }
        // Count only adapters with a default gateway (avoids double-counting virtual switches)
        var sel = new List<NetworkInterface>();
        foreach (var n in up) if (HasGateway(n)) sel.Add(n);
        if (sel.Count == 0) sel = up;

        long rx = 0, tx = 0;
        var names = new List<string>();
        var cur = new Dictionary<string, long[]>();
        foreach (var n in sel) {
            long r, s;
            try { var st = n.GetIPStatistics(); r = st.BytesReceived; s = st.BytesSent; }
            catch { continue; }
            cur[n.Id] = new long[] { r, s };
            long[] p;
            if (prev.TryGetValue(n.Id, out p)) { rx += Delta(r, p[0]); tx += Delta(s, p[1]); }
            names.Add(n.Name);
        }
        prev = cur;
        if (dt > 0) { RxRate = rx / dt; TxRate = tx / dt; RxTotal += rx; TxTotal += tx; }
        IfName = names.Count > 0 ? string.Join(", ", names.ToArray()) : "no connection";
    }

    // ---- window styles ----
    public static void SetClickThrough(IntPtr h, bool on) {
        int s = GetWindowLong(h, -20);
        s |= 0x80;                       // WS_EX_TOOLWINDOW (hide from Alt+Tab)
        if (on) s |= 0x20; else s &= ~0x20;  // WS_EX_TRANSPARENT
        SetWindowLong(h, -20, s);
    }
}
'@

# ---------- settings ----------
$S = [ordered]@{ Left = $null; Top = $null; Opacity = 0.92; Locked = $false; ClickThrough = $false; Compact = $false; Bits = $false }
if (Test-Path $SettingsFile) {
    try {
        $j = Get-Content $SettingsFile -Raw | ConvertFrom-Json
        foreach ($p in $j.PSObject.Properties) { if ($S.Contains($p.Name)) { $S[$p.Name] = $p.Value } }
    } catch { Write-Log "settings: $_" }
}
function Save-Settings {
    try {
        $S.Left = $win.Left; $S.Top = $win.Top
        ($S | ConvertTo-Json) | Set-Content -Path $SettingsFile -Encoding UTF8
    } catch { Write-Log "save: $_" }
}

# ---------- UI ----------
[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="SysMon Widget" WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize" SizeToContent="Height" Width="250"
        FontFamily="Segoe UI" FontSize="12" Foreground="#E8EAED" UseLayoutRounding="True">
  <Border x:Name="Root" CornerRadius="10" Background="#E6161A22" BorderBrush="#30FFFFFF" BorderThickness="1" Padding="12,9,12,10">
    <StackPanel>
      <Grid Margin="0,0,0,7">
        <TextBlock x:Name="HostText" FontSize="11" FontWeight="SemiBold" Foreground="#9AA0A6"/>
        <TextBlock x:Name="UptimeText" FontSize="11" Foreground="#80868B" HorizontalAlignment="Right"/>
      </Grid>

      <Grid>
        <TextBlock Text="CPU" Foreground="#9AA0A6"/>
        <TextBlock x:Name="CpuText" HorizontalAlignment="Right" FontWeight="SemiBold" Foreground="#4FC3F7"/>
      </Grid>
      <Canvas x:Name="CpuGraph" Height="26" Margin="0,3,0,8" ClipToBounds="True" Background="#0FFFFFFF">
        <Polygon x:Name="CpuPoly" Fill="#334FC3F7" Stroke="#4FC3F7" StrokeThickness="1.2"/>
      </Canvas>

      <Grid>
        <TextBlock Text="RAM" Foreground="#9AA0A6"/>
        <TextBlock x:Name="RamText" HorizontalAlignment="Right" FontWeight="SemiBold" Foreground="#B388FF"/>
      </Grid>
      <Canvas x:Name="RamGraph" Height="26" Margin="0,3,0,8" ClipToBounds="True" Background="#0FFFFFFF">
        <Polygon x:Name="RamPoly" Fill="#33B388FF" Stroke="#B388FF" StrokeThickness="1.2"/>
      </Canvas>

      <StackPanel x:Name="Drives"/>

      <Grid Margin="0,2,0,0">
        <TextBlock Text="NET" Foreground="#9AA0A6"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
          <TextBlock x:Name="DownText" FontWeight="SemiBold" Foreground="#69F0AE" Margin="0,0,10,0"/>
          <TextBlock x:Name="UpText" FontWeight="SemiBold" Foreground="#FFB74D"/>
        </StackPanel>
      </Grid>
      <Canvas x:Name="NetGraph" Height="36" Margin="0,3,0,4" ClipToBounds="True" Background="#0FFFFFFF">
        <Polygon x:Name="DownPoly" Fill="#3369F0AE" Stroke="#69F0AE" StrokeThickness="1.2"/>
        <Polygon x:Name="UpPoly" Fill="#33FFB74D" Stroke="#FFB74D" StrokeThickness="1.2"/>
        <TextBlock x:Name="NetScale" FontSize="9" Foreground="#80868B" Canvas.Left="3" Canvas.Top="1"/>
      </Canvas>
      <Grid>
        <TextBlock x:Name="IfText" FontSize="10" Foreground="#80868B" TextTrimming="CharacterEllipsis" MaxWidth="110" HorizontalAlignment="Left"/>
        <TextBlock x:Name="TotalText" FontSize="10" Foreground="#80868B" HorizontalAlignment="Right"/>
      </Grid>
    </StackPanel>
  </Border>
</Window>
'@
$win = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$ui = @{}
foreach ($n in 'Root','HostText','UptimeText','CpuText','CpuGraph','CpuPoly','RamText','RamGraph','RamPoly','Drives',
               'DownText','UpText','NetGraph','DownPoly','UpPoly','NetScale','IfText','TotalText') { $ui[$n] = $win.FindName($n) }

$bc = New-Object System.Windows.Media.BrushConverter
function Get-Brush([string]$c) { $bc.ConvertFromString($c) }
$GW = 224          # graph width (window 250 - padding - border)
$HistLen = 60
$hCpu = New-Object 'System.Collections.Generic.List[double]'
$hRam = New-Object 'System.Collections.Generic.List[double]'
$hRx  = New-Object 'System.Collections.Generic.List[double]'
$hTx  = New-Object 'System.Collections.Generic.List[double]'
$ARROW_DN = [char]0x2193; $ARROW_UP = [char]0x2191

$ui.HostText.Text = $env:COMPUTERNAME

# ---------- drives ----------
$driveRows = @()
foreach ($d in [IO.DriveInfo]::GetDrives()) {
    if ($d.DriveType -ne 'Fixed' -or -not $d.IsReady) { continue }
    $sp = New-Object Windows.Controls.StackPanel; $sp.Margin = '0,0,0,8'
    $g  = New-Object Windows.Controls.Grid
    $l  = New-Object Windows.Controls.TextBlock; $l.Text = 'DISK ' + $d.Name.TrimEnd('\'); $l.Foreground = Get-Brush '#9AA0A6'
    $v  = New-Object Windows.Controls.TextBlock; $v.HorizontalAlignment = 'Right'; $v.FontWeight = 'SemiBold'; $v.Foreground = Get-Brush '#E8EAED'
    [void]$g.Children.Add($l); [void]$g.Children.Add($v)
    $track = New-Object Windows.Controls.Border; $track.Height = 4; $track.CornerRadius = 2; $track.Margin = '0,4,0,0'; $track.Background = Get-Brush '#1FFFFFFF'
    $bar = New-Object Windows.Controls.Border; $bar.HorizontalAlignment = 'Left'; $bar.CornerRadius = 2; $bar.Width = 0
    $track.Child = $bar
    [void]$sp.Children.Add($g); [void]$sp.Children.Add($track)
    [void]$ui.Drives.Children.Add($sp)
    $driveRows += [pscustomobject]@{ Name = $d.Name; Value = $v; Bar = $bar }
}
function Update-Drives {
    foreach ($r in $driveRows) {
        try {
            $d = New-Object IO.DriveInfo $r.Name
            $tot = $d.TotalSize / 1GB; $used = ($d.TotalSize - $d.AvailableFreeSpace) / 1GB
            $f = if ($tot -gt 0) { $used / $tot } else { 0 }
            $r.Value.Text = '{0:0} / {1:0} GB  {2:0}%' -f $used, $tot, ($f * 100)
            $r.Bar.Width = [Math]::Max(0, $GW * $f)
            $r.Bar.Background = Get-Brush $(if ($f -gt 0.9) { '#FF6E6E' } elseif ($f -gt 0.75) { '#FFD54F' } else { '#81C784' })
        } catch {}
    }
}

# ---------- helpers ----------
function Add-Hist($list, [double]$v) { $list.Add($v); if ($list.Count -gt $HistLen) { $list.RemoveAt(0) } }

function Set-Area($poly, $list, [double]$max, [double]$h) {
    $pts = New-Object System.Windows.Media.PointCollection
    $n = $list.Count
    if ($n -lt 2) { $poly.Points = $pts; return }
    $step = $GW / ($HistLen - 1)
    $x0 = $GW - ($n - 1) * $step
    $pts.Add((New-Object System.Windows.Point($x0, ($h + 1))))
    for ($i = 0; $i -lt $n; $i++) {
        $y = $h - [Math]::Min(1.0, $list[$i] / $max) * ($h - 2)
        $pts.Add((New-Object System.Windows.Point(($x0 + $i * $step), $y)))
    }
    $pts.Add((New-Object System.Windows.Point($GW, ($h + 1))))
    $poly.Points = $pts
}

function Format-Rate([double]$bytesPerSec) {
    if ($S.Bits) {
        $v = $bytesPerSec * 8; $u = 'bps', 'Kbps', 'Mbps', 'Gbps'; $k = 1000
    } else {
        $v = $bytesPerSec; $u = 'B/s', 'KB/s', 'MB/s', 'GB/s'; $k = 1024
    }
    $i = 0; while ($v -ge $k -and $i -lt 3) { $v /= $k; $i++ }
    if ($i -eq 0) { '{0:0} {1}' -f $v, $u[$i] } elseif ($v -lt 10) { '{0:0.0} {1}' -f $v, $u[$i] } else { '{0:0} {1}' -f $v, $u[$i] }
}
function Format-Size([double]$b) {
    $u = 'B', 'KB', 'MB', 'GB', 'TB'; $i = 0
    while ($b -ge 1024 -and $i -lt 4) { $b /= 1024; $i++ }
    if ($i -lt 2) { '{0:0} {1}' -f $b, $u[$i] } else { '{0:0.0} {1}' -f $b, $u[$i] }
}
function Format-Uptime {
    $t = [TimeSpan]::FromMilliseconds([SysMon]::UptimeMs())
    if ($t.Days -gt 0) { 'up {0}d {1}h {2}m' -f $t.Days, $t.Hours, $t.Minutes } else { 'up {0}h {1}m' -f $t.Hours, $t.Minutes }
}

$tick = 0
function Update-All {
    $script:tick++
    $cpu = [SysMon]::Cpu()
    [SysMon]::Mem()
    [SysMon]::Net()
    if ($script:tick -eq 1) { return }   # first sample only primes deltas

    Add-Hist $hCpu $cpu
    Add-Hist $hRam ([SysMon]::MemLoad)
    Add-Hist $hRx  ([SysMon]::RxRate)
    Add-Hist $hTx  ([SysMon]::TxRate)

    $ui.CpuText.Text  = '{0:0}%' -f $cpu
    $ui.RamText.Text  = '{0:0.0} / {1:0.0} GB  {2:0}%' -f [SysMon]::MemUsedGB, [SysMon]::MemTotalGB, [SysMon]::MemLoad
    $ui.DownText.Text = "$ARROW_DN " + (Format-Rate ([SysMon]::RxRate))
    $ui.UpText.Text   = "$ARROW_UP " + (Format-Rate ([SysMon]::TxRate))
    $ui.TotalText.Text = "session $ARROW_DN " + (Format-Size ([SysMon]::RxTotal)) + "  $ARROW_UP " + (Format-Size ([SysMon]::TxTotal))
    $ui.IfText.Text    = [SysMon]::IfName
    $ui.IfText.ToolTip = [SysMon]::IfName
    $ui.CpuText.Foreground = Get-Brush $(if ($cpu -ge 90) { '#FF6E6E' } elseif ($cpu -ge 70) { '#FFD54F' } else { '#4FC3F7' })

    if ($script:tick % 30 -eq 2) { $ui.UptimeText.Text = Format-Uptime }
    if ($script:tick % 15 -eq 2) { Update-Drives }

    if (-not $S.Compact) {
        Set-Area $ui.CpuPoly $hCpu 100 26
        Set-Area $ui.RamPoly $hRam 100 26
        $max = 8KB
        foreach ($v in $hRx) { if ($v -gt $max) { $max = $v } }
        foreach ($v in $hTx) { if ($v -gt $max) { $max = $v } }
        $max *= 1.15
        Set-Area $ui.DownPoly $hRx $max 36
        Set-Area $ui.UpPoly   $hTx $max 36
        $ui.NetScale.Text = Format-Rate $max
    }

    if ($tray) {
        $tip = "CPU {0:0}%  RAM {1:0}%`n$ARROW_DN {2}  $ARROW_UP {3}" -f $cpu, [SysMon]::MemLoad, (Format-Rate ([SysMon]::RxRate)), (Format-Rate ([SysMon]::TxRate))
        if ($tip.Length -gt 63) { $tip = $tip.Substring(0, 63) }
        $tray.Text = $tip
    }
}

function Set-Compact([bool]$on) {
    $vis = if ($on) { 'Collapsed' } else { 'Visible' }
    foreach ($c in 'CpuGraph', 'RamGraph', 'NetGraph') { $ui[$c].Visibility = $vis }
}

$hwnd = [IntPtr]::Zero
function Set-ClickThrough([bool]$on) {
    $S.ClickThrough = $on
    if ($script:hwnd -ne [IntPtr]::Zero) { [SysMon]::SetClickThrough($script:hwnd, $on) }
    if ($trayClick) { $trayClick.Checked = $on }
    if ($miClick)   { $miClick.IsChecked = $on }
}

# ---------- position ----------
$vsL = [System.Windows.SystemParameters]::VirtualScreenLeft
$vsT = [System.Windows.SystemParameters]::VirtualScreenTop
$vsR = $vsL + [System.Windows.SystemParameters]::VirtualScreenWidth
$vsB = $vsT + [System.Windows.SystemParameters]::VirtualScreenHeight
if ($null -ne $S.Left -and $S.Left -ge ($vsL - 50) -and $S.Left -le ($vsR - 60) -and $S.Top -ge ($vsT - 10) -and $S.Top -le ($vsB - 40)) {
    $win.Left = $S.Left; $win.Top = $S.Top
} else {
    $wa = [System.Windows.SystemParameters]::WorkArea
    $win.Left = $wa.Right - 250 - 12; $win.Top = $wa.Top + 12
}
$win.Opacity = [double]$S.Opacity
Set-Compact ([bool]$S.Compact)

$win.Add_MouseLeftButtonDown({
    if (-not $S.Locked) { try { $win.DragMove(); Save-Settings } catch {} }
})

# ---------- context menu ----------
function New-MI([string]$header, [bool]$checkable = $false, [bool]$checked = $false) {
    $m = New-Object Windows.Controls.MenuItem
    $m.Header = $header; $m.IsCheckable = $checkable; $m.IsChecked = $checked
    $m
}
$cm = New-Object Windows.Controls.ContextMenu

$miCompact = New-MI 'Compact (hide graphs)' $true ([bool]$S.Compact)
$miCompact.Add_Click({ $S.Compact = $miCompact.IsChecked; Set-Compact $S.Compact; Save-Settings })
$miLock = New-MI 'Lock position' $true ([bool]$S.Locked)
$miLock.Add_Click({ $S.Locked = $miLock.IsChecked; Save-Settings })
$miClick = New-MI 'Click-through (undo from tray icon)' $true ([bool]$S.ClickThrough)
$miClick.Add_Click({ Set-ClickThrough $miClick.IsChecked; Save-Settings })
$miBits = New-MI 'Show network in bits (Mbps)' $true ([bool]$S.Bits)
$miBits.Add_Click({ $S.Bits = $miBits.IsChecked; Save-Settings })

$miOpacity = New-MI 'Opacity'
foreach ($o in 1.0, 0.92, 0.8, 0.65, 0.5) {
    $mo = New-MI ('{0:0}%' -f ($o * 100))
    $mo.Tag = $o
    $mo.Add_Click({ param($sender) $win.Opacity = [double]$sender.Tag; $S.Opacity = [double]$sender.Tag; Save-Settings })
    [void]$miOpacity.Items.Add($mo)
}
$miStartup = New-MI 'Start with Windows' $true (Test-Path $StartupLnk)
$miStartup.Add_Click({
    try {
        if ($miStartup.IsChecked) { New-Launcher $StartupLnk } else { Remove-Item $StartupLnk -ErrorAction SilentlyContinue }
    } catch { Write-Log "startup: $_" }
    $miStartup.IsChecked = Test-Path $StartupLnk
})
$miReset = New-MI 'Reset session totals'
$miReset.Add_Click({ [SysMon]::ResetTotals() })
$miTaskMgr = New-MI 'Open Task Manager'
$miTaskMgr.Add_Click({ Start-Process taskmgr.exe })
$miExit = New-MI 'Exit'
$miExit.Add_Click({ Exit-App })

foreach ($m in $miCompact, $miLock, $miClick, $miBits, $miOpacity, (New-Object Windows.Controls.Separator),
               $miStartup, $miReset, $miTaskMgr, (New-Object Windows.Controls.Separator), $miExit) { [void]$cm.Items.Add($m) }
$ui.Root.ContextMenu = $cm

# ---------- tray icon ----------
$bmp = New-Object System.Drawing.Bitmap 32, 32
$gr = [System.Drawing.Graphics]::FromImage($bmp)
$gr.SmoothingMode = 'AntiAlias'
$gr.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 22, 26, 34))), 0, 0, 32, 32)
$gr.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 79, 195, 247))), 4, 18, 6, 10)
$gr.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 179, 136, 255))), 13, 8, 6, 20)
$gr.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 105, 240, 174))), 22, 13, 6, 15)
$gr.Dispose()

$tray = New-Object System.Windows.Forms.NotifyIcon
$tray.Icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$tray.Text = 'SysMon Widget'
$trayMenu = New-Object System.Windows.Forms.ContextMenuStrip
$trayShow = $trayMenu.Items.Add('Show / hide widget')
$trayClick = New-Object System.Windows.Forms.ToolStripMenuItem 'Click-through'
$trayClick.CheckOnClick = $true
$trayClick.Checked = [bool]$S.ClickThrough
[void]$trayMenu.Items.Add($trayClick)
$trayCenter = $trayMenu.Items.Add('Move to top-right corner')
[void]$trayMenu.Items.Add('-')
$trayExit = $trayMenu.Items.Add('Exit')
$tray.ContextMenuStrip = $trayMenu

function Switch-Visible { if ($win.IsVisible) { $win.Hide() } else { $win.Show(); $win.Topmost = $true } }
$trayShow.Add_Click({ Switch-Visible })
$tray.Add_DoubleClick({ Switch-Visible })
$trayClick.Add_Click({ Set-ClickThrough $trayClick.Checked; Save-Settings })
$trayCenter.Add_Click({
    $wa = [System.Windows.SystemParameters]::WorkArea
    $win.Left = $wa.Right - $win.ActualWidth - 12; $win.Top = $wa.Top + 12
    $win.Show(); Save-Settings
})
$trayExit.Add_Click({ Exit-App })
$tray.Visible = $true

# ---------- lifecycle ----------
$app = New-Object System.Windows.Application
$app.ShutdownMode = 'OnExplicitShutdown'

function Exit-App {
    try { $timer.Stop(); Save-Settings } catch {}
    try { $tray.Visible = $false; $tray.Dispose() } catch {}
    $app.Shutdown()
}

$win.Add_SourceInitialized({
    $script:hwnd = (New-Object System.Windows.Interop.WindowInteropHelper $win).Handle
    Set-ClickThrough ([bool]$S.ClickThrough)
})

$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(1)
$timer.Add_Tick({
    try { Update-All } catch { Write-Log "tick: $_" }
    # re-assert topmost occasionally (some fullscreen apps knock it down)
    if ($script:tick % 10 -eq 0 -and $win.IsVisible) { $win.Topmost = $false; $win.Topmost = $true }
})

try {
    Update-All
    Update-Drives
    $ui.UptimeText.Text = Format-Uptime
    $timer.Start()
    [void]$app.Run($win)
} catch {
    Write-Log "fatal: $_`n$($_.ScriptStackTrace)"
} finally {
    try { $tray.Visible = $false; $tray.Dispose() } catch {}
    $mutex.ReleaseMutex()
}
