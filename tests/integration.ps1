# Untrail integration tests. They create throwaway fake apps, run the real delete path and check the results.
#
#   ./tests/integration.ps1 -Exe dist/Untrail.exe            user-level scenario (+ machine-wide one when run as admin)
#
# Only fixtures named "Zephyr ..." are created and removed. Meant for CI runners and test machines:
# the machine-wide scenario writes to Program Files, ProgramData and HKLM.
param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$work = Join-Path $env:TEMP ("untrail-it-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$failures = New-Object System.Collections.Generic.List[string]

function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $failures.Add($what) }
}
function Gone($p, $what) { Check (-not (Test-Path -LiteralPath $p)) "removed: $what" }
function Kept($p, $what) { Check (Test-Path -LiteralPath $p) "kept: $what" }
function Fill($dir, $file = 'data.bin', $kb = 64) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $dir $file), (New-Object byte[] ($kb * 1KB)))
}
function Shortcut($lnk, $target) {
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk); $s.TargetPath = $target; $s.Save()
}
function Compile($out, $code, $product, $company) {
    $base = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($out))
    $code | Set-Content "$base.cs"
    "[assembly: System.Reflection.AssemblyProduct(`"$product`")] [assembly: System.Reflection.AssemblyCompany(`"$company`")]" | Set-Content "$base.info.cs"
    & $csc /nologo /target:winexe /out:$out "$base.cs" "$base.info.cs" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "compile failed: $out" }
}
function Purge($target, $report, [string[]]$extra) {
    $args = @('--purge', "`"$target`"", '--yes', '--permanent', '--via-helper', '--out', "`"$report`"") + $extra
    $p = Start-Process $Exe -ArgumentList $args -Wait -PassThru
    Get-Content $report | ForEach-Object { Write-Host "    | $_" }
    return $p.ExitCode
}

$appExe = 'class P { static void Main() { } }'
# Behaves like an NSIS uninstaller: copies itself to Temp, starts the copy and exits at once.
# The copy works for 4 seconds, removes the app's program file and Installed-apps entry, and
# writes a log into the install folder. If Untrail didn't wait for it, that log would be left behind.
$uninstaller = @'
using System; using System.Diagnostics; using System.IO; using System.Threading; using Microsoft.Win32;
class U { static int Main(string[] a) {
  string self = Process.GetCurrentProcess().MainModule.FileName;
  if (a.Length > 2 && a[0] == "--child") {
    Thread.Sleep(4000);
    try { File.Delete(Path.Combine(a[1], "ZephyrPaint.exe")); } catch { }
    Directory.CreateDirectory(a[1]);
    File.WriteAllText(Path.Combine(a[1], "uninstall.log"), "written by the uninstaller at the very end");
    Registry.CurrentUser.DeleteSubKeyTree(a[2], false);
    return 0;
  }
  string tmp = Path.Combine(Path.GetTempPath(), "zephyr-un-" + Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(tmp);
  string copy = Path.Combine(tmp, "Un_A.exe");
  File.Copy(self, copy);
  Process.Start(new ProcessStartInfo(copy, "--child \"" + a[0] + "\" \"" + a[1] + "\"") { UseShellExecute = false });
  return 0;
} }
'@

try {
    # ------------------------------------------------------------------ 0. delete allowlist
    Write-Host "`n== Guard self-test"
    $p = Start-Process $Exe -ArgumentList '--selftest', "`"$work\guard.txt`"" -Wait -PassThru
    Get-Content "$work\guard.txt" | Where-Object { $_ -match 'FAIL|PASSED' } | ForEach-Object { Write-Host "    | $_" }
    Check ($p.ExitCode -eq 0) "protected paths and registry keys are refused"

    # ------------------------------------------------------------------ 1. per-user app
    Write-Host "`n== Per-user app with an NSIS-style uninstaller"
    $name = 'Zephyr Paint Pro'
    $install = "$env:LOCALAPPDATA\Programs\$name"
    $uKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\ZephyrPaintPro'
    New-Item -ItemType Directory -Force $install | Out-Null
    Compile "$install\ZephyrPaint.exe" $appExe $name 'Zephyr Labs'
    Compile "$install\uninst.exe" $uninstaller "$name Uninstaller" 'Zephyr Labs'
    Fill $install 'resources.pak' 512
    Fill "$env:APPDATA\$name"
    Fill "$env:LOCALAPPDATA\$name\Cache"
    Fill "$env:LOCALAPPDATA\zephyr-paint-pro-updater"
    Fill "$env:APPDATA\Zephyr Labs\$name"
    Fill "$env:APPDATA\Zephyr Labs\Other App" 'KEEP.txt' 1                    # same publisher, different app
    Fill "$env:LOCALAPPDATA\ZephyrPaintProHelper" 'KEEP.txt' 1               # similar name: Review only
    Fill "$env:USERPROFILE\AppData\LocalLow\$name" 'old.dat' 1               # same name but untouched for years
    $old = (Get-Date).AddYears(-2)
    Get-ChildItem "$env:USERPROFILE\AppData\LocalLow\$name" | ForEach-Object { $_.LastWriteTime = $old }
    (Get-Item "$env:USERPROFILE\AppData\LocalLow\$name").LastWriteTime = $old
    $sm = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Zephyr Labs"
    New-Item -ItemType Directory -Force $sm | Out-Null
    Shortcut "$sm\$name.lnk" "$install\ZephyrPaint.exe"
    Shortcut "$work\$name.lnk" "$install\ZephyrPaint.exe"
    New-Item -Force "HKCU:\Software\Zephyr Labs\$name" | Out-Null
    New-Item -Force 'HKCU:\Software\Zephyr Labs\Other App' | Out-Null
    New-Item -Force "HKCU:\$uKey" | Out-Null
    Set-ItemProperty "HKCU:\$uKey" DisplayName "$name 2.4.1"
    Set-ItemProperty "HKCU:\$uKey" Publisher 'Zephyr Labs Ltd'
    Set-ItemProperty "HKCU:\$uKey" InstallLocation $install
    Set-ItemProperty "HKCU:\$uKey" UninstallString "`"$install\uninst.exe`" `"$install`" `"$uKey`""
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' ZephyrPaintPro "`"$install\ZephyrPaint.exe`" --tray"

    $code = Purge "$work\$name.lnk" "$work\user.txt" @('--run-uninstaller')
    $report = Get-Content "$work\user.txt" -Raw
    Check ($code -eq 0) "purge exit code 0 (got $code)"
    Check ($report -match 'UNINSTALLER exit code 0, finished') "waited for the uninstaller's hand-off copy to finish"
    Check ($report -match '\[Likely\].*LocalLow\\Zephyr Paint Pro.*untouched since before') "old same-name folder downgraded to Review"
    Gone $install 'install folder (including the log the uninstaller wrote last)'
    Gone "$env:APPDATA\$name" 'Roaming data'
    Gone "$env:LOCALAPPDATA\$name" 'Local cache'
    Gone "$env:LOCALAPPDATA\zephyr-paint-pro-updater" 'updater cache'
    Gone "$env:APPDATA\Zephyr Labs\$name" "data inside the publisher's folder"
    Gone $sm 'Start menu folder'
    Gone "$work\$name.lnk" 'dropped shortcut'
    Gone "HKCU:\Software\Zephyr Labs\$name" 'settings key'
    Gone "HKCU:\$uKey" 'Installed-apps entry'
    Check (-not (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ZephyrPaintPro -ErrorAction SilentlyContinue)) 'removed: start-up entry'
    Kept "$env:APPDATA\Zephyr Labs\Other App\KEEP.txt" "other app by the same publisher"
    Kept 'HKCU:\Software\Zephyr Labs\Other App' "other app's settings key"
    Kept "$env:LOCALAPPDATA\ZephyrPaintProHelper\KEEP.txt" 'similar-named folder (Review)'
    Kept "$env:USERPROFILE\AppData\LocalLow\$name\old.dat" 'old same-name folder (Review)'
    $backup = ([regex]::Match($report, 'BACKUP\s+(.+)')).Groups[1].Value.Trim()
    $regs = @(Get-ChildItem -LiteralPath $backup -Filter *.reg -ErrorAction SilentlyContinue)
    Check ($regs.Count -ge 2) "registry backups written before deleting ($($regs.Count) .reg files)"
    Check (Test-Path -LiteralPath "$backup\removal-log.txt") 'removal log written'

    # ------------------------------------------------------------------ 2. machine-wide app (admin only)
    if ($isAdmin) {
        Write-Host "`n== Machine-wide app through the admin helper"
        $mName = 'Zephyr Machine Tool'
        $pf = "$env:ProgramFiles\$mName"
        $mKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ZephyrMachineTool'
        New-Item -ItemType Directory -Force $pf | Out-Null
        Compile "$pf\zmt.exe" $appExe $mName 'Zephyr Labs'
        Fill "$env:ProgramData\$mName"
        Fill "${env:ProgramFiles(x86)}\$mName" 'KEEP.txt' 1                    # belongs to another registered app
        New-Item -Force "HKLM:\SOFTWARE\$mName" | Out-Null
        New-Item -Force $mKey | Out-Null
        Set-ItemProperty $mKey DisplayName $mName; Set-ItemProperty $mKey Publisher 'Zephyr Labs'; Set-ItemProperty $mKey InstallLocation $pf
        $oKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ContosoHelper'
        New-Item -Force $oKey | Out-Null
        Set-ItemProperty $oKey DisplayName 'Contoso Helper'; Set-ItemProperty $oKey InstallLocation "${env:ProgramFiles(x86)}\$mName"
        Shortcut "$work\$mName.lnk" "$pf\zmt.exe"

        $code = Purge "$work\$mName.lnk" "$work\machine.txt" @()
        Check ($code -eq 0) "purge exit code 0 (got $code)"
        Gone $pf 'Program Files folder'
        Gone "$env:ProgramData\$mName" 'ProgramData folder'
        Gone "HKLM:\SOFTWARE\$mName" 'HKLM settings key'
        Gone $mKey 'HKLM Installed-apps entry'
        Kept "${env:ProgramFiles(x86)}\$mName\KEEP.txt" "same-named folder owned by another installed app"
        Kept $oKey "the other app's Installed-apps entry"
    }
    else { Write-Host "`n== Machine-wide scenario skipped (needs admin)" }
}
finally {
    # Clean every fixture, whatever happened.
    $paths = @("$env:LOCALAPPDATA\Programs\Zephyr Paint Pro", "$env:APPDATA\Zephyr Paint Pro", "$env:LOCALAPPDATA\Zephyr Paint Pro",
        "$env:LOCALAPPDATA\zephyr-paint-pro-updater", "$env:APPDATA\Zephyr Labs", "$env:LOCALAPPDATA\ZephyrPaintProHelper",
        "$env:USERPROFILE\AppData\LocalLow\Zephyr Paint Pro", "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Zephyr Labs", $work)
    if ($isAdmin) { $paths += @("$env:ProgramFiles\Zephyr Machine Tool", "$env:ProgramData\Zephyr Machine Tool", "${env:ProgramFiles(x86)}\Zephyr Machine Tool") }
    $paths | ForEach-Object { Remove-Item -LiteralPath $_ -Recurse -Force -ErrorAction SilentlyContinue }
    Get-ChildItem $env:TEMP -Directory -Filter 'zephyr-un-*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    $keys = @('HKCU:\Software\Zephyr Labs', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ZephyrPaintPro')
    if ($isAdmin) { $keys += @('HKLM:\SOFTWARE\Zephyr Machine Tool', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ZephyrMachineTool', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ContosoHelper') }
    $keys | ForEach-Object { Remove-Item -LiteralPath $_ -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ZephyrPaintPro -ErrorAction SilentlyContinue
    Get-ChildItem "$([Environment]::GetFolderPath('MyDocuments'))\Untrail Backups" -Directory -Filter 'Zephyr*' -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "All checks passed" -ForegroundColor Green
