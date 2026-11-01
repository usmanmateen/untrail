# Records docs/untrail-demo.gif: the real Untrail removing a disposable demo app ("Northwind Notes").
#
#   powershell -ExecutionPolicy Bypass -File tools/demo/record-demo.ps1 -Exe dist/Untrail.exe
#   python tools/demo/make_gif.py <frames folder printed at the end> docs/untrail-demo.gif
#
# Needs an unlocked, interactive Windows desktop at 100% display scaling. For about a minute it moves the
# mouse and clicks: don't touch the mouse or keyboard meanwhile. Before every click it checks that the window
# under the cursor belongs to Untrail or the demo, and aborts otherwise. Only "Northwind ..." demo files are
# created, and they're removed afterwards. Untrail runs with --hide-user, so paths show as %USERPROFILE%.
#
# -Screenshots <dir>: no recording and no mouse. Opens the demo app in Untrail, drives the buttons through
# UI Automation and saves window screenshots (main window, results) with PrintWindow. Works on a locked screen.
param([Parameter(Mandatory)][string]$Exe, [int]$Fps = 12, [string]$Screenshots)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$here = $PSScriptRoot
$work = Join-Path $env:TEMP 'untrail-demo'
$frames = Join-Path $env:TEMP ('untrail-frames-' + (Get-Date -Format 'HHmmss'))
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes

Add-Type @'
using System; using System.Runtime.InteropServices; using System.Threading;
public static class Desk {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy, mouseData, dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public int type; public MOUSEINPUT mi; int pad1; int pad2; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, int flags);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, int flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  static void Button(int flags) {
    var i = new INPUT[1]; i[0].type = 0; i[0].mi.dwFlags = flags;
    SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Down() { Button(0x0002); }
  public static void Up() { Button(0x0004); }
  // Eased, human-looking move; drags need real intermediate mouse moves.
  public static void Glide(int x, int y, int ms) {
    POINT s; GetCursorPos(out s);
    int steps = Math.Max(1, ms / 15);
    for (int k = 1; k <= steps; k++) {
      double t = (double)k / steps; t = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
      SetCursorPos((int)(s.X + (x - s.X) * t), (int)(s.Y + (y - s.Y) * t));
      Thread.Sleep(15);
    }
  }
  public static int PidAt(int x, int y) {
    var p = new POINT { X = x, Y = y };
    IntPtr h = GetAncestor(WindowFromPoint(p), 2);
    int pid; GetWindowThreadProcessId(h, out pid); return pid;
  }
}
'@

$allowed = New-Object System.Collections.Generic.HashSet[int]
function Click($x, $y, $ms = 700) {
    [Desk]::Glide($x, $y, $ms); Start-Sleep -Milliseconds 120
    $pid_ = [Desk]::PidAt($x, $y)
    if (-not $allowed.Contains($pid_)) { throw "Aborting: the window under the cursor isn't Untrail or the demo (pid $pid_)." }
    [Desk]::Down(); Start-Sleep -Milliseconds 70; [Desk]::Up()
}
function Center($el) { $r = $el.Current.BoundingRectangle; return @([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
function Find($root, [scriptblock]$match, $timeoutSec = 20) {
    $end = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $end) {
        foreach ($el in $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
            try { if (& $match $el) { return $el } } catch { }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for a UI element"
}
function TopWindow($pid_, $titleLike, $timeoutSec = 30) {
    $end = (Get-Date).AddSeconds($timeoutSec)
    $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $pid_)
    while ((Get-Date) -lt $end) {
        foreach ($w in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $cond)) {
            if ($w.Current.Name -like $titleLike) { return $w }
            foreach ($c in $w.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
                if ($c.Current.ControlType -eq [Windows.Automation.ControlType]::Window -and $c.Current.Name -like $titleLike) { return $c }
            }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for window '$titleLike'"
}
function Claim($p) { if (Test-Path -LiteralPath $p) { throw "Demo path already exists, refusing to touch it: $p" }; $p }
function Big($dir, $file, $mb) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    $f = [IO.File]::Create((Join-Path $dir $file)); $f.SetLength([long]$mb * 1MB); $f.Close()
}

# ---------------------------------------------------------------- demo app
$name = 'Northwind Notes'
$install = Claim "$env:LOCALAPPDATA\Programs\$name"
$roaming = Claim "$env:APPDATA\$name"
$local = Claim "$env:LOCALAPPDATA\$name"
$updater = Claim "$env:LOCALAPPDATA\northwind-notes-updater"
$dot = Claim "$env:USERPROFILE\.northwindnotes"
$tmpDir = Claim "$env:TEMP\$name"
$sm = Claim "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Northwind Labs"
$key = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\NorthwindNotes'
Claim "HKCU:\$key" | Out-Null; Claim 'HKCU:\Software\Northwind Labs' | Out-Null
if ((Get-PSDrive C).Free -lt 6GB) { throw "Needs about 2 GB of temporary demo files; less than 6 GB free on C:" }
New-Item -ItemType Directory -Force $work, $frames | Out-Null
$demoDirs = @($install, $roaming, $local, $updater, $dot, $tmpDir, $sm, $work)

$area = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$W = 1280; $H = 720; $strip = 300
if ($area.Width -lt $W -or $area.Height -lt $H) { throw "The primary screen's work area must be at least ${W}x${H}" }
$X = $area.X + [int](($area.Width - $W) / 2); $Y = $area.Y + [int](($area.Height - $H) / 2)

try {
    function Compile($out, $src, $product) {
        $info = Join-Path $work ([guid]::NewGuid().ToString('N') + '.cs')
        "[assembly: System.Reflection.AssemblyProduct(`"$product`")] [assembly: System.Reflection.AssemblyCompany(`"Northwind Labs`")]" | Set-Content $info
        & $csc /nologo /target:winexe /out:$out /reference:System.Windows.Forms.dll /reference:System.Drawing.dll $src $info | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "compile failed: $out" }
    }
    $appSrc = Join-Path $work 'app.cs'; 'class P { static void Main() { } }' | Set-Content $appSrc
    New-Item -ItemType Directory -Force $install, "$install\resources", $updater | Out-Null
    Compile "$install\$name.exe" $appSrc $name
    Compile "$install\Uninstall $name.exe" "$here\DemoUninstaller.cs" "$name Uninstall"
    Compile "$updater\installer.exe" $appSrc $name
    Compile "$work\DemoStage.exe" "$here\DemoStage.cs" 'Untrail demo stage'
    Big $install 'resources.pak' 214; Big "$install\resources" 'app.asar' 12
    Big "$roaming\Cache" 'data_1' 1240; '{}' | Set-Content "$roaming\Local State"
    Big "$local\GPUCache" 'data_0' 338
    ('{ "installPath": "' + ($install -replace '\\', '\\') + '" }') | Set-Content "$local\config.json"
    Big $updater 'pending.bin' 96
    Big $dot 'index.db' 61
    Big $tmpDir 'crash.log' 3
    New-Item -ItemType Directory -Force $sm | Out-Null
    $wsh = New-Object -ComObject WScript.Shell
    foreach ($l in @("$sm\$name.lnk", "$work\$name.lnk")) { $s = $wsh.CreateShortcut($l); $s.TargetPath = "$install\$name.exe"; $s.Save() }
    New-Item -Force "HKCU:\Software\Northwind Labs\$name" | Out-Null
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' NorthwindNotes "`"$install\$name.exe`" --background"
    New-Item -Force "HKCU:\$key" | Out-Null
    $ux = $X + $strip + [int](($W - $strip) / 2); $uy = $Y + [int]($H / 2)
    $props = @{ DisplayName = $name; DisplayVersion = '4.2.1'; Publisher = 'Northwind Labs'; InstallLocation = $install
                InstallDate = (Get-Date -Format yyyyMMdd); DisplayIcon = "$install\$name.exe"
                UninstallString = "`"$install\Uninstall $name.exe`" `"$install`" `"$key`" $ux $uy" }
    foreach ($k in $props.Keys) { Set-ItemProperty "HKCU:\$key" $k $props[$k] }

    if ($Screenshots) {
        Add-Type -ReferencedAssemblies System.Drawing @'
using System; using System.Drawing; using System.Runtime.InteropServices;
public static class Shot {
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, int flags);
  public static void Save(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    using (var b = new Bitmap(r.R - r.L, r.B - r.T)) using (var g = Graphics.FromImage(b)) {
      IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); b.Save(path);
    }
  }
}
'@
        New-Item -ItemType Directory -Force $Screenshots | Out-Null
        $untrail = Start-Process $Exe -ArgumentList '--hide-user', "`"$work\$name.lnk`"" -PassThru
        [void]$allowed.Add($untrail.Id)   # so the finally block closes it whatever happens
        $main = TopWindow $untrail.Id 'Untrail*'
        $hwnd = [IntPtr]$main.Current.NativeWindowHandle
        Find $main { param($e) $e.Current.Name -like 'Scan finished*' } 40 | Out-Null
        Start-Sleep 1
        [Shot]::Save($hwnd, "$Screenshots\screenshot.png")
        $perm = Find $main { param($e) $e.Current.Name -like 'Delete permanently*' } 5
        $perm.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
        $btn = Find $main { param($e) $e.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $e.Current.Name -like '*Clean' } 5
        $btn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $confirm = TopWindow $untrail.Id 'Confirm removal' 10
        $yes = Find $confirm { param($e) $e.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $e.Current.Name -eq 'Yes' } 5
        $yes.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $wait = TopWindow $untrail.Id 'Uninstalling*' 15
        Start-Sleep -Milliseconds 800
        [Shot]::Save([IntPtr]$wait.Current.NativeWindowHandle, "$Screenshots\waiting.png")
        $results = TopWindow $untrail.Id "Untrail*$name*" 90
        Start-Sleep 1
        [Shot]::Save([IntPtr]$results.Current.NativeWindowHandle, "$Screenshots\results.png")
        $done = Find $results { param($e) $e.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and ($e.Current.Name -like 'Done*' -or $e.Current.Name -like 'Review*') } 5
        $done.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 1
        [Shot]::Save($hwnd, "$Screenshots\after.png")
        Stop-Process -Id $untrail.Id -ErrorAction SilentlyContinue
        Write-Host "Screenshots: $Screenshots"
        return
    }

    # ------------------------------------------------------------ windows
    $stage = Start-Process "$work\DemoStage.exe" -ArgumentList $X, $Y, $W, $H, $strip, "`"$work\$name.lnk`"", "`"$frames`"", $Fps -PassThru
    $untrail = Start-Process $Exe -ArgumentList '--hide-user' -PassThru
    [void]$allowed.Add($stage.Id); [void]$allowed.Add($untrail.Id)
    $main = TopWindow $untrail.Id 'Untrail*'
    $hwnd = [IntPtr]$main.Current.NativeWindowHandle
    # Topmost for the take, so nothing else on the desktop covers it (its dialogs are owned, so they stay above it).
    [void][Desk]::SetWindowPos($hwnd, [IntPtr](-1), $X + $strip, $Y, $W - $strip, $H, 0x0040)
    [void][Desk]::SetForegroundWindow($hwnd)
    Start-Sleep 2
    [Desk]::SetCursorPos($X + 700, $Y + 560) | Out-Null

    # ------------------------------------------------------------ the take
    New-Item "$frames\start" -ItemType File | Out-Null
    Start-Sleep -Milliseconds 1500
    # 1-2. drag the demo app's shortcut into Untrail's drop zone
    $iconX = $X + [int]($strip / 2); $iconY = $Y + 195
    [Desk]::Glide($iconX, $iconY, 900); Start-Sleep -Milliseconds 300
    if (-not $allowed.Contains([Desk]::PidAt($iconX, $iconY))) { throw 'Aborting: the demo shortcut is covered by another window.' }
    [Desk]::Down(); Start-Sleep -Milliseconds 150
    $r = New-Object Desk+RECT; [void][Desk]::GetWindowRect($hwnd, [ref]$r)
    [Desk]::Glide($iconX + 30, $iconY + 10, 200)
    [Desk]::Glide($r.L + 150, $r.T + 200, 1400); Start-Sleep -Milliseconds 300
    if (-not $allowed.Contains([Desk]::PidAt($r.L + 150, $r.T + 200))) { [Desk]::Up(); throw 'Aborting: drop target is not Untrail.' }
    [Desk]::Up()
    # 3-4. scan: wait for it to finish
    Find $main { param($e) $e.Current.Name -like 'Scan finished*' } 40 | Out-Null
    Start-Sleep -Milliseconds 900
    # 5. review: hover a few rows, including one that needs review
    foreach ($pat in '*Roaming\Northwind Notes', '*northwind-notes-updater', '*.northwindnotes') {
        $row = Find $main { param($e) $e.Current.ControlType -eq [Windows.Automation.ControlType]::ListItem -and $e.Current.Name -like $pat } 5
        $c = Center $row; [Desk]::Glide($c[0] - 120, $c[1], 650); Start-Sleep -Milliseconds 650
    }
    $perm = Find $main { param($e) $e.Current.Name -like 'Delete permanently*' } 5
    $c = Center $perm; Click ($c[0] - 160) $c[1] 700; Start-Sleep -Milliseconds 600
    # 6. Uninstall & Clean
    $btn = Find $main { param($e) $e.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $e.Current.Name -like '*Clean' } 5
    $c = Center $btn; Click $c[0] $c[1] 900
    $confirm = TopWindow $untrail.Id 'Confirm removal' 10
    Start-Sleep -Milliseconds 1600
    $yes = Find $confirm { param($e) $e.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $e.Current.Name -eq 'Yes' } 5
    $c = Center $yes; Click $c[0] $c[1] 700
    # 7. the demo app's own uninstaller runs (its window closes by itself)
    Start-Sleep 1
    Get-Process -Name "Uninstall $name" -ErrorAction SilentlyContinue | ForEach-Object { [void]$allowed.Add($_.Id) }
    [Desk]::Glide($X + $W - 120, $Y + $H - 40, 800)
    # 8-10. cleanup, fresh scan, results
    $results = TopWindow $untrail.Id "Untrail*$name*" 90
    Start-Sleep -Milliseconds 4500
    $done = Find $results { param($e) $e.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and ($e.Current.Name -like 'Done*' -or $e.Current.Name -like 'Review*') } 5
    $c = Center $done; Click $c[0] $c[1] 900
    Start-Sleep -Milliseconds 2200
    New-Item "$frames\stop" -ItemType File | Out-Null
    $stage.WaitForExit(15000) | Out-Null
    Stop-Process -Id $untrail.Id -ErrorAction SilentlyContinue
    Write-Host "Frames: $frames"
}
finally {
    foreach ($id in $allowed) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
    Start-Sleep 1
    foreach ($d in $demoDirs) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath "HKCU:\$key", 'HKCU:\Software\Northwind Labs' -Recurse -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name NorthwindNotes -ErrorAction SilentlyContinue
    Get-ChildItem "$([Environment]::GetFolderPath('MyDocuments'))\Untrail Backups" -Directory -Filter 'Northwind*' -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
