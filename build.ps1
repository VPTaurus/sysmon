<#
  Builds dist\SysMon.exe with the .NET Framework 4.x compiler that ships with Windows (no SDK needed).
  -Install  also copies it to %LOCALAPPDATA%\Programs\SysMon Widget and adds a Start Menu shortcut.
  -Version  release version, e.g. 1.2.0 (CI passes the tag). Without it: <last tag>-dev+<commit>.
#>
param([switch]$Install, [string]$Version = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf  = "$fw\WPF"
$dist = Join-Path $root 'dist'
$obj  = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $dist, $obj | Out-Null

# ---------- version ----------
if (-not $Version) {
    $sha = ''; $tag = ''
    try { $sha = (git -C $root rev-parse --short HEAD 2>$null) } catch {}
    try { $tag = (git -C $root describe --tags --abbrev=0 2>$null) } catch {}
    $base = if ($tag -match '^v?(\d+\.\d+\.\d+)') { $Matches[1] } else { '0.1.0' }
    $Version = "$base-dev" + $(if ($sha) { "+$sha" } else { '' })
}
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)') { throw "Version must start with major.minor.patch, got '$Version'" }
$numeric = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
@"
using System.Reflection;
[assembly: AssemblyTitle("SysMon Widget")]
[assembly: AssemblyProduct("SysMon Widget")]
[assembly: AssemblyDescription("Always-on-top system resource and network monitor")]
[assembly: AssemblyVersion("$numeric")]
[assembly: AssemblyFileVersion("$numeric")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content (Join-Path $obj 'AssemblyInfo.g.cs') -Encoding UTF8
"Version $Version"

# ---------- icon (multi-size, PNG entries) ----------
Add-Type -AssemblyName System.Drawing
function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $r = [Math]::Max(2, $s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($s - $r - 1, 0, $r, $r, 270, 90)
    $path.AddArc($s - $r - 1, $s - $r - 1, $r, $r, 0, 90); $path.AddArc(0, $s - $r - 1, $r, $r, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 24, 28, 38))), $path)
    $bw = $s * 0.18; $gap = $s * 0.08; $x = ($s - (3 * $bw + 2 * $gap)) / 2; $base = $s * 0.80
    $cols = @([System.Drawing.Color]::FromArgb(255, 79, 195, 247), [System.Drawing.Color]::FromArgb(255, 179, 136, 255), [System.Drawing.Color]::FromArgb(255, 105, 240, 174))
    $hts = @(0.32, 0.58, 0.44)
    for ($i = 0; $i -lt 3; $i++) {
        $h = $s * $hts[$i]
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $cols[$i]), [float]($x + $i * ($bw + $gap)), [float]($base - $h), [float]$bw, [float]$h)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
$sizes = 16, 20, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) { , (New-IconPng $s) }
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
$icoPath = Join-Path $root 'app.ico'
[IO.File]::WriteAllBytes($icoPath, $ico.ToArray())

# ---------- compile ----------
$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll", "$fw\System.Xaml.dll",
    "$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Drawing.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Web.Extensions.dll"
) | ForEach-Object { "/r:`"$_`"" }
$out = Join-Path $dist 'SysMon.exe'
$srcs = @(Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { "`"$($_.FullName)`"" }) + "`"$(Join-Path $obj 'AssemblyInfo.g.cs')`""
$cscArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', "/win32icon:`"$icoPath`"", "/out:`"$out`"") + $refs + $srcs
& "$fw\csc.exe" @cscArgs | Where-Object { $_ -notmatch 'C# 5|language versions|go.microsoft.com|^\s*$|This compiler is provided' }
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
"Built $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"

# ---------- install (per user, no admin) ----------
if ($Install) {
    # the app's own installer: copies to %LOCALAPPDATA%\Programs\SysMon Widget, Start Menu shortcut, Settings > Apps entry
    $dest = Join-Path $env:LOCALAPPDATA 'Programs\SysMon Widget'
    $p = Start-Process $out -ArgumentList '--install', '--quiet' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Install failed (see %APPDATA%\SysMonWidget\error.log)" }
    "Installed to $dest (Start Menu + Settings > Apps)"
}
