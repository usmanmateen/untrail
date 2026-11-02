# Untrail integration tests. They create throwaway fake apps, run the real delete path and check the results.
#
#   ./tests/integration.ps1 -Exe dist/Untrail.exe
#
# Fixtures are fake apps named "Zephyr ...", plus one "Paint Pro" name-collision decoy. Before creating anything
# the script checks that none of its fixture paths already exist, and stops if one does, so it never touches
# real data. Admin-only parts (Program Files, ProgramData, HKLM, symlinks) run only when elevated, as on CI.
param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$work = Join-Path $env:TEMP ("untrail-it-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$failures = New-Object System.Collections.Generic.List[string]
$cleanupPaths = New-Object System.Collections.Generic.List[string]
$cleanupKeys = New-Object System.Collections.Generic.List[string]
$lockers = New-Object System.Collections.Generic.List[object]
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstRoot = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'

function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $failures.Add($what) }
}
function Gone($p, $what) { Check (-not (Test-Path -LiteralPath $p)) "removed: $what" }
function Kept($p, $what) { Check (Test-Path -LiteralPath $p) "kept: $what" }
# Every fixture path goes through Claim: refuses to run if it already exists, and registers it for cleanup.
function Claim($p) {
    if (Test-Path -LiteralPath $p) { throw "Fixture path already exists, refusing to touch it: $p" }
    if ($p -like 'HK*') { $cleanupKeys.Add($p) } else { $cleanupPaths.Add($p) }
    return $p
}
function Fill($dir, $file = 'data.bin', $kb = 64) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $dir $file), (New-Object byte[] ($kb * 1KB)))
}
function Text($dir, $file, $content) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    [IO.File]::WriteAllText((Join-Path $dir $file), $content)
}
function Junction($link, $target) { cmd /c mklink /J "$link" "$target" | Out-Null }
function Shortcut($lnk, $target) {
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk); $s.TargetPath = $target; $s.Save()
}
function Compile($out, $code, $product, $company) {
    $base = Join-Path $work ([guid]::NewGuid().ToString('N'))
    $code | Set-Content "$base.cs"
    "[assembly: System.Reflection.AssemblyProduct(`"$product`")] [assembly: System.Reflection.AssemblyCompany(`"$company`")]" | Set-Content "$base.info.cs"
    & $csc /nologo /target:winexe /out:$out "$base.cs" "$base.info.cs" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "compile failed: $out" }
}
function Size($p) {
    # Logical bytes, not following links (the same way Untrail measures).
    if (Test-Path -LiteralPath $p -PathType Leaf) { return (Get-Item -LiteralPath $p).Length }
    $total = 0
    foreach ($e in Get-ChildItem -LiteralPath $p -Force -ErrorAction SilentlyContinue) {
        if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        if ($e.PSIsContainer) { $total += Size $e.FullName } else { $total += $e.Length }
    }
    return $total
}
function Purge($target, $report, [string[]]$extra, [switch]$Recycle) {
    $a = @('--purge', "`"$target`"", '--yes', '--via-helper', '--out', "`"$report`"") + $extra
    if (-not $Recycle) { $a += '--permanent' }
    $p = Start-Process $Exe -ArgumentList $a -Wait -PassThru
    Get-Content $report | Where-Object { $_ -match '^(UNINSTALLER|RESULT|STORAGE|VERIFY|REMAINING|HEADLINE|STOPPED|\[Sure|\[Likely)' } |
        ForEach-Object { Write-Host "    | $_" }
    return $p.ExitCode
}
function Storage($report, $field) {
    $sum = 0
    foreach ($m in [regex]::Matches((Get-Content $report -Raw), "STORAGE \w: .*?\b$field=(-?\d+)")) { $sum += [long]$m.Groups[1].Value }
    return $sum
}
function ResultBytes($report) {
    $sum = 0
    foreach ($m in [regex]::Matches((Get-Content $report -Raw), 'RESULT \w+\s+bytes=(\d+)')) { $sum += [long]$m.Groups[1].Value }
    return $sum
}

# A fake app: program + uninstaller in AppData\Local\Programs\<name>, an Installed-apps entry, and a shortcut.
# Uninstaller modes: handoff (NSIS-style: copies itself to Temp, starts the copy, exits; the copy works 4 s,
# removes the program, a 300 KB part and the entry, and writes a log last), cancel (exit 1602, changes nothing),
# partial (removes only the entry), none.
$uninstallerCode = @'
using System; using System.Diagnostics; using System.IO; using System.Threading; using Microsoft.Win32;
class U {
  static int Main(string[] a) {
    if (a[0] == "--child") { Thread.Sleep(4000); Finish(a[1], a[2]); return 0; }
    if (a[0] == "cancel") { Thread.Sleep(500); return 1602; }
    if (a[0] == "partial") { Registry.CurrentUser.DeleteSubKeyTree(a[2], false); return 0; }
    string tmp = Path.Combine(Path.GetTempPath(), "zephyr-un-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    string copy = Path.Combine(tmp, "Un_A.exe");
    File.Copy(Process.GetCurrentProcess().MainModule.FileName, copy);
    Process.Start(new ProcessStartInfo(copy, "--child \"" + a[1] + "\" \"" + a[2] + "\"") { UseShellExecute = false });
    return 0;
  }
  static void Finish(string install, string key) {
    foreach (string f in new[] { "ZephyrApp.exe", "bigpart.bin" }) try { File.Delete(Path.Combine(install, f)); } catch { }
    Directory.CreateDirectory(install);
    File.WriteAllText(Path.Combine(install, "uninstall.log"), "written by the uninstaller at the very end");
    Registry.CurrentUser.DeleteSubKeyTree(key, false);
  }
}
'@
$uninstallerExe = Join-Path $work 'uninst-template.exe'

function New-App($name, $mode) {
    $install = Claim "$env:LOCALAPPDATA\Programs\$name"
    $key = "$uninstRoot\Zz" + ($name -replace '\W', '')
    Claim "HKCU:\$key" | Out-Null
    New-Item -ItemType Directory -Force $install | Out-Null
    Compile "$install\ZephyrApp.exe" 'class P { static void Main() { } }' $name 'Zephyr Labs'
    Copy-Item $uninstallerExe "$install\uninst.exe"
    Fill $install 'resources.pak' 512
    Fill $install 'bigpart.bin' 300
    New-Item -Force "HKCU:\$key" | Out-Null
    Set-ItemProperty "HKCU:\$key" DisplayName "$name 2.4.1"
    Set-ItemProperty "HKCU:\$key" Publisher 'Zephyr Labs Ltd'
    Set-ItemProperty "HKCU:\$key" InstallLocation $install
    Set-ItemProperty "HKCU:\$key" InstallDate (Get-Date -Format yyyyMMdd)
    if ($mode -ne 'none') { Set-ItemProperty "HKCU:\$key" UninstallString "`"$install\uninst.exe`" $mode `"$install`" `"$key`"" }
    $lnk = Join-Path $work "$name.lnk"
    Shortcut $lnk "$install\ZephyrApp.exe"
    return @{ Install = $install; Key = "HKCU:\$key"; Lnk = $lnk; Exe = "$install\ZephyrApp.exe" }
}
function Evidence($dir, $install) {
    # A config file that mentions the install folder, the way real apps store paths in JSON.
    Text $dir 'config.json' ('{ "installPath": "' + ($install -replace '\\', '\\') + '" }')
}

# Removes our own fixtures from the Recycle Bin by reading its $I records; nothing else is touched.
function Remove-OwnRecycled($pattern) {
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root -match '^[A-Z]:\\$' })) {
        $bin = Join-Path $drive.Root "`$Recycle.Bin\$sid"
        if (-not (Test-Path -LiteralPath $bin)) { continue }
        foreach ($i in Get-ChildItem -LiteralPath $bin -Force -Filter '$I*' -ErrorAction SilentlyContinue) {
            try {
                $b = [IO.File]::ReadAllBytes($i.FullName)
                $path = if ([BitConverter]::ToInt64($b, 0) -ge 2) { [Text.Encoding]::Unicode.GetString($b, 28, [BitConverter]::ToInt32($b, 24) * 2 - 2) }
                        else { [Text.Encoding]::Unicode.GetString($b, 24, 520).TrimEnd([char]0) }
                if ($path -like $pattern) {
                    $r = Join-Path $bin ('$R' + $i.Name.Substring(2))
                    Remove-Item -LiteralPath $r -Recurse -Force -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath $i.FullName -Force
                }
            } catch { }
        }
    }
}

try {
    Compile $uninstallerExe $uninstallerCode 'Zephyr Uninstaller' 'Zephyr Labs'

    # ------------------------------------------------------------------ 0. delete allowlist
    Write-Host "`n== Guard self-test: protected paths and keys"
    $p = Start-Process $Exe -ArgumentList '--selftest', "`"$work\guard.txt`"" -Wait -PassThru
    Get-Content "$work\guard.txt" | Where-Object { $_ -match 'FAIL|PASSED' } | ForEach-Object { Write-Host "    | $_" }
    Check ($p.ExitCode -eq 0) "protected paths and registry keys are refused"

    # ------------------------------------------------------------------ 1. admin helper refuses out-of-scope items
    Write-Host "`n== Admin helper: items that don't belong to the app"
    $decoy = Claim "$env:APPDATA\Zephyr Decoy Data"
    Fill $decoy 'KEEP.txt' 1
    Claim 'HKCU:\Software\Zephyr Decoy Vendor' | Out-Null
    New-Item 'HKCU:\Software\Zephyr Decoy Vendor' | Out-Null
    $p = Start-Process $Exe -ArgumentList '--helper-test', "`"$decoy`"", "`"$work\helper.txt`"" -Wait -PassThru
    Get-Content "$work\helper.txt" | ForEach-Object { Write-Host "    | $_" }
    Check ($p.ExitCode -eq 0) "helper refused a folder and a registry key outside the app's scope"
    Kept "$decoy\KEEP.txt" 'decoy folder sent to the helper'
    Kept 'HKCU:\Software\Zephyr Decoy Vendor' 'decoy registry key sent to the helper'

    # ------------------------------------------------------------------ 2. main scenario
    Write-Host "`n== Per-user app: hand-off uninstaller, ownership evidence, decoys, links, storage"
    $name = 'Zephyr Paint Pro'
    $app = New-App $name 'handoff'
    Fill "$($app.Install)\resources" 'app.asar' 128                                     # an Electron app
    # Leftovers, each with its own kind of ownership evidence:
    $roaming = Claim "$env:APPDATA\$name";            Fill $roaming; Text $roaming 'Local State' '{}'          # Electron data folder
    $local = Claim "$env:LOCALAPPDATA\$name";         Fill "$local\Cache"; Evidence $local $app.Install           # mentions install folder
    $updater = Claim "$env:LOCALAPPDATA\zephyr-paint-pro-updater"
    New-Item -ItemType Directory $updater | Out-Null
    Compile "$updater\installer.exe" 'class P { static void Main() { } }' $name 'Zephyr Labs'                  # app's own program
    $pubDir = Claim "$env:APPDATA\Zephyr Labs";       Fill "$pubDir\$name"                                        # inside publisher folder
    Fill "$pubDir\Other App" 'KEEP.txt' 1                                                                         # other app, same publisher
    $sm = Claim "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Zephyr Labs"
    New-Item -ItemType Directory $sm | Out-Null
    Shortcut "$sm\$name.lnk" $app.Exe
    Claim 'HKCU:\Software\Zephyr Labs' | Out-Null
    New-Item -Force "HKCU:\Software\Zephyr Labs\$name" | Out-Null
    New-Item -Force 'HKCU:\Software\Zephyr Labs\Other App' | Out-Null
    if (-not (Test-Path $runKey)) { New-Item $runKey | Out-Null }   # never -Force: that would wipe existing start-up entries
    Set-ItemProperty $runKey ZephyrPaintPro "`"$($app.Exe)`" --tray"
    # Decoys that must survive:
    $helper = Claim "$env:LOCALAPPDATA\ZephyrPaintProHelper";        Fill $helper 'KEEP.txt' 1                   # similar name
    $oldDir = Claim "$env:USERPROFILE\AppData\LocalLow\$name";       Fill $oldDir 'old.dat' 1                    # same name, untouched for years
    $old = (Get-Date).AddYears(-2)
    Get-ChildItem $oldDir | ForEach-Object { $_.LastWriteTime = $old }; (Get-Item $oldDir).LastWriteTime = $old
    $collide = Claim "$env:APPDATA\Paint Pro";                       Text $collide 'settings.json' '{ "owner": "Contoso" }'  # another app's name
    $noLink = Claim "$env:APPDATA\ZephyrPaintPro";                   Fill $noLink 'KEEP.txt' 1                   # unreadable: check fails
    icacls "$noLink" /deny "*${sid}:(RD)" | Out-Null
    Claim 'HKCU:\Software\Zephyr Paint Pro' | Out-Null
    New-Item 'HKCU:\Software\Zephyr Paint Pro' | Out-Null; Set-ItemProperty 'HKCU:\Software\Zephyr Paint Pro' Theme 'dark'  # same-name key, no link
    $victim = Join-Path $work 'victim';  Fill $victim 'precious.txt' 1
    Junction "$local\linked" $victim                                                                             # junction inside a leftover
    $victim2 = Join-Path $work 'victim2'; Fill $victim2 'precious.txt' 1
    $tempLink = Claim "$env:TEMP\$name"; Junction $tempLink $victim2                                             # a leftover that is a junction

    $expected = 0
    foreach ($x in @($app.Install, $roaming, $local, $updater, "$pubDir\$name", $sm, $app.Lnk)) { $expected += Size $x }
    $r = "$work\main.txt"
    $code = Purge $app.Lnk $r @('--run-uninstaller')
    $text = Get-Content $r -Raw
    Check ($code -eq 0) "purge exit code 0 (got $code)"
    Check ($text -match 'UNINSTALLER state=Completed') "waited for the hand-off copy; uninstall judged complete"
    Check ($text -match 'HEADLINE Cleanup complete\.') "headline says cleanup complete (backed by the follow-up scan)"
    Check ($text -match 'VERIFY completed safe_remaining=0') "follow-up scan ran and found nothing safe-to-remove left"
    Check ($text -match "Electron data folder") "evidence: Electron data folder"
    Check ($text -match "mention the app's install folder") "evidence: files mention the install folder"
    Check ($text -match "own program files") "evidence: the app's own program in the updater cache"
    Check ($text -match '\[Likely\].*LocalLow\\Zephyr Paint Pro.*untouched since before') "old same-name folder only for review"
    Check ($text -match '\[Likely\].*Roaming\\Paint Pro.*nothing in it links') "name collision only for review"
    Check ($text -match "\[Likely\].*Roaming\\ZephyrPaintPro.*Couldn't check") "unreadable folder only for review (check failed)"
    Check ($text -match "\[Likely\].*Software\\Zephyr Paint Pro.*nothing in it points") "same-name registry key only for review"
    Check ($text -match "\[Likely\].*Temp\\Zephyr Paint Pro.*a link to another folder") "junction leftover only for review"
    Gone $app.Install 'install folder, including the log the uninstaller wrote last'
    foreach ($x in @($roaming, $local, $updater, "$pubDir\$name", $sm, $app.Lnk, "HKCU:\Software\Zephyr Labs\$name", $app.Key)) { Gone $x (Split-Path $x -Leaf) }
    Check (-not (Get-ItemProperty $runKey -Name ZephyrPaintPro -ErrorAction SilentlyContinue)) 'removed: start-up entry'
    Kept "$pubDir\Other App\KEEP.txt" 'other app by the same publisher'
    Kept 'HKCU:\Software\Zephyr Labs\Other App' "other app's settings key"
    foreach ($x in @("$helper\KEEP.txt", "$oldDir\old.dat", "$collide\settings.json", $noLink, 'HKCU:\Software\Zephyr Paint Pro', $tempLink)) { Kept $x (Split-Path $x -Leaf) }
    Kept "$victim\precious.txt" 'target of a junction inside a deleted folder'
    Kept "$victim2\precious.txt" 'target of a junction leftover'
    $byUninst = Storage $r 'uninstaller'; $deleted = Storage $r 'deleted'
    Check ($byUninst -ge 300KB) "storage: the uninstaller's own removals measured ($byUninst bytes)"
    Check ($deleted -eq (ResultBytes $r)) "storage: per-drive total equals the per-item results ($deleted bytes)"
    Check (($byUninst + $deleted) -eq $expected) "storage: uninstaller + Untrail = everything found, no double counting ($($byUninst + $deleted) vs $expected)"
    Check ((Storage $r 'recycled') -eq 0) 'storage: nothing claimed as recycled in permanent mode'
    Check ($text -match 'STORAGE C: .*free_change=-?\d+') 'storage: measured free-space change reported per drive'
    $backup = ([regex]::Match($text, 'BACKUP\s+(.+)')).Groups[1].Value.Trim() -replace '^%USERPROFILE%', $env:USERPROFILE
    $regs = @(Get-ChildItem -LiteralPath $backup -Filter *.reg -ErrorAction SilentlyContinue)
    Check ($regs.Count -ge 2) "registry backups written before deleting ($($regs.Count) .reg files)"

    # ------------------------------------------------------------------ 3. cancelled uninstaller
    Write-Host "`n== Cancelled uninstaller: nothing is deleted unless you insist"
    $c = New-App 'Zephyr Cancel Demo' 'cancel'
    $cData = Claim "$env:APPDATA\Zephyr Cancel Demo"; Fill $cData; Evidence $cData $c.Install
    $code = Purge $c.Lnk "$work\cancel.txt" @('--run-uninstaller')
    $text = Get-Content "$work\cancel.txt" -Raw
    Check ($code -eq 4) "purge stops with exit code 4 (got $code)"
    Check ($text -match 'UNINSTALLER state=NoChange exit=1602') 'cancel recognised: no change, exit code 1602'
    Check ($text -notmatch 'RESULT') 'no deletion attempted'
    Check ($text -match 'HEADLINE Stopped:') 'report says it stopped, not that cleanup ran'
    Kept $c.Exe 'program'; Kept $cData 'app data'; Kept $c.Key 'Installed-apps entry'
    $code = Purge $c.Lnk "$work\cancel2.txt" @('--run-uninstaller', '--clean-anyway')
    Check ($code -eq 0) "explicit 'clean up anyway' then removes it (exit $code)"
    Gone $c.Install 'program folder after explicit manual cleanup'; Gone $cData 'app data after explicit manual cleanup'

    # ------------------------------------------------------------------ 4. partial uninstall
    Write-Host "`n== Partial uninstall: one signal gone is not success"
    $pa = New-App 'Zephyr Partial Demo' 'partial'
    $pData = Claim "$env:APPDATA\Zephyr Partial Demo"; Fill $pData; Evidence $pData $pa.Install
    $code = Purge $pa.Lnk "$work\partial.txt" @('--run-uninstaller')
    $text = Get-Content "$work\partial.txt" -Raw
    Check ($code -eq 4) "purge stops with exit code 4 (got $code)"
    Check ($text -match 'UNINSTALLER state=Partial .*entry_present=False program_present=True') 'entry gone but program still there = partial'
    Kept $pa.Exe 'program'; Kept $pData 'app data'

    # ------------------------------------------------------------------ 4b. large folders: recent use can be deep inside
    Write-Host "`n== Large folders: a recent change deep inside counts, an old tree stays for review"
    $d = New-App 'Zephyr Deep Demo' 'none'
    Fill "$($d.Install)\resources" 'app.asar' 128                                       # an Electron app
    $old = (Get-Date).AddYears(-2)
    # An Electron data folder with over 300 old entries; when it's in use, the only recent file is three levels
    # down, after all of them in directory order.
    function Old-Tree($root, [switch]$InUse) {
        Text $root 'Local State' '{}'
        foreach ($i in 0..399) { Fill $root ('f{0:D3}.dat' -f $i) 1 }
        Fill "$root\zz-profile\a\b" 'recent.dat' 1
        Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { -not ($InUse -and $_.Name -eq 'recent.dat') } |
            ForEach-Object { $_.LastWriteTime = $old }
        foreach ($p in @("$root\zz-profile\a\b", "$root\zz-profile\a", "$root\zz-profile", $root)) { (Get-Item -LiteralPath $p).LastWriteTime = $old }
    }
    $used = Claim "$env:APPDATA\Zephyr Deep Demo";      Old-Tree $used -InUse   # written to recently, deep inside
    $stale = Claim "$env:LOCALAPPDATA\Zephyr Deep Demo"; Old-Tree $stale         # really untouched for two years
    $code = Purge $d.Lnk "$work\deep.txt" @()
    $text = Get-Content "$work\deep.txt" -Raw
    Check ($text -match "\[Sure\s*\].*Roaming\\Zephyr Deep Demo.*Electron data folder") 'large folder used recently (deep inside) is Safe to remove'
    Check ($text -match '\[Likely\].*Local\\Zephyr Deep Demo.*untouched since before') 'large folder untouched for years stays for review'
    Gone $used 'large folder in use'
    Kept "$stale\f000.dat" 'large untouched folder'

    # ------------------------------------------------------------------ 5. registry backup failure
    Write-Host "`n== Registry backup fails: those entries are left alone"
    $b = New-App 'Zephyr Backup Demo' 'none'
    $bData = Claim "$env:APPDATA\Zephyr Backup Demo"; Fill $bData; Evidence $bData $b.Install
    Set-ItemProperty $runKey ZephyrBackupDemo "`"$($b.Exe)`""
    $code = Purge $b.Lnk "$work\backup.txt" @('--simulate-backup-failure')
    $text = Get-Content "$work\backup.txt" -Raw
    Check ($code -eq 2) "purge reports failures (exit $code)"
    Check ($text -match "RESULT Failed .*Uninstall\\ZzZephyrBackupDemo.*Couldn't back it up") 'Installed-apps entry refused without a backup'
    Kept $b.Key 'Installed-apps entry (no backup, no delete)'
    Check ([bool](Get-ItemProperty $runKey -Name ZephyrBackupDemo -ErrorAction SilentlyContinue)) 'kept: start-up entry (no backup, no delete)'
    Gone $b.Install 'files still removed'; Gone $bData 'app data still removed'

    # ------------------------------------------------------------------ 6. locked file
    Write-Host "`n== File locked by a running process"
    $l = New-App 'Zephyr Locked Demo' 'none'
    $lData = Claim "$env:APPDATA\Zephyr Locked Demo"; Fill $lData 'locked.bin' 256; Evidence $lData $l.Install
    $lockers.Add((Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-Command',
        "`$f = [IO.File]::Open('$lData\locked.bin', 'Open', 'Read', 'None'); Start-Sleep 300"))
    Start-Sleep 2
    $code = Purge $l.Lnk "$work\locked.txt" @()
    $text = Get-Content "$work\locked.txt" -Raw
    if ($isAdmin) {
        Check ($code -eq 5) "admin: exit 5, waiting for a restart (got $code)"
        Check ($text -match 'RESULT PendingRestart .*Zephyr Locked Demo.*in use') 'locked file scheduled for restart and reported'
        Check ((Storage "$work\locked.txt" 'pending') -ge 256KB) 'pending-restart bytes reported separately'
    } else {
        Check ($code -eq 2) "exit 2, couldn't remove (got $code)"
        Check ($text -match 'RESULT Failed .*left=\d+.*Zephyr Locked Demo') 'locked file reported as not removed, with bytes left'
        Check ((Storage "$work\locked.txt" 'left') -ge 256KB) "bytes that couldn't be removed reported separately"
    }
    Check ($text -match 'REMAINING .*Zephyr Locked Demo') 'follow-up scan lists what is still there'
    Gone $l.Install 'the rest of the app'

    # ------------------------------------------------------------------ 7. Recycle Bin
    Write-Host "`n== Recycle Bin mode"
    $rc = New-App 'Zephyr Recycle Demo' 'none'
    $rcData = Claim "$env:APPDATA\Zephyr Recycle Demo"; Fill $rcData 'data.bin' 200; Evidence $rcData $rc.Install
    $code = Purge $rc.Lnk "$work\recycle.txt" @() -Recycle
    $text = Get-Content "$work\recycle.txt" -Raw
    Check ($code -eq 0) "purge exit code 0 (got $code)"
    Check ($text -match 'RESULT Recycled .*Zephyr Recycle Demo') 'items moved to the Recycle Bin'
    Check ((Storage "$work\recycle.txt" 'recycled') -ge 200KB) 'recycled bytes reported as recycled'
    Check ((Storage "$work\recycle.txt" 'deleted') -eq 0) 'nothing claimed as permanently deleted'
    Gone $rc.Install 'program folder (moved to the bin)'; Gone $rcData 'app data (moved to the bin)'

    # ------------------------------------------------------------------ 8. shared install folder
    Write-Host "`n== Install folder shared with another installed app"
    $s = New-App 'Zephyr Suite' 'none'
    Fill "$($s.Install)\Helper" 'helper.dll' 16
    Claim "HKCU:\$uninstRoot\ZzContosoHelper" | Out-Null
    New-Item "HKCU:\$uninstRoot\ZzContosoHelper" | Out-Null
    Set-ItemProperty "HKCU:\$uninstRoot\ZzContosoHelper" DisplayName 'Contoso Helper'
    Set-ItemProperty "HKCU:\$uninstRoot\ZzContosoHelper" InstallLocation "$($s.Install)\Helper"
    $code = Purge $s.Lnk "$work\suite.txt" @()
    $text = Get-Content "$work\suite.txt" -Raw
    Check ($text -match '\[Likely\].*Programs\\Zephyr Suite.*also holds: Contoso Helper') 'shared install folder only for review'
    Kept "$($s.Install)\Helper\helper.dll" "the other app's files"; Kept $s.Exe 'shared folder left for you to decide'
    # Even when you tick it yourself, a folder holding another installed app is refused at delete time.
    Shortcut $s.Lnk $s.Exe
    $code = Purge $s.Lnk "$work\suite2.txt" @('--include-review')
    $text = Get-Content "$work\suite2.txt" -Raw
    Check ($text -match 'RESULT Failed .*Programs\\Zephyr Suite\s+-- Refused: it holds files of another installed app \(Contoso Helper\)') 'ticked shared folder refused at delete time'
    Kept "$($s.Install)\Helper\helper.dll" "the other app's files, even when the shared folder is ticked"

    # ------------------------------------------------------------------ 9. machine-wide app (admin only)
    if ($isAdmin) {
        Write-Host "`n== Machine-wide app through the admin helper"
        $mName = 'Zephyr Machine Tool'
        $pf = Claim "$env:ProgramFiles\$mName"
        $mKey = Claim 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ZephyrMachineTool'
        Claim "HKLM:\SOFTWARE\$mName" | Out-Null
        New-Item -ItemType Directory $pf | Out-Null
        Compile "$pf\zmt.exe" 'class P { static void Main() { } }' $mName 'Zephyr Labs'
        $pd = Claim "$env:ProgramData\$mName"; Fill $pd; Evidence $pd $pf
        $victim3 = Join-Path $work 'victim3'; Fill $victim3 'precious.txt' 1
        New-Item -ItemType SymbolicLink -Path "$pd\symlinked" -Target $victim3 | Out-Null       # directory symlink inside a leftover
        $other = Claim "${env:ProgramFiles(x86)}\$mName"; Fill $other 'KEEP.txt' 1             # belongs to another registered app
        New-Item -Force "HKLM:\SOFTWARE\$mName" | Out-Null; Set-ItemProperty "HKLM:\SOFTWARE\$mName" Path $pf
        New-Item -Force $mKey | Out-Null
        Set-ItemProperty $mKey DisplayName $mName; Set-ItemProperty $mKey Publisher 'Zephyr Labs'; Set-ItemProperty $mKey InstallLocation $pf
        $oKey = Claim 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ContosoHelper'
        New-Item -Force $oKey | Out-Null
        Set-ItemProperty $oKey DisplayName 'Contoso Helper'; Set-ItemProperty $oKey InstallLocation $other
        Shortcut "$work\$mName.lnk" "$pf\zmt.exe"

        $code = Purge "$work\$mName.lnk" "$work\machine.txt" @()
        Check ($code -eq 0) "purge exit code 0 (got $code)"
        Gone $pf 'Program Files folder'; Gone $pd 'ProgramData folder'
        Gone "HKLM:\SOFTWARE\$mName" 'HKLM settings key'; Gone $mKey 'HKLM Installed-apps entry'
        Kept "$victim3\precious.txt" 'target of a directory symlink inside a deleted folder'
        Kept "$other\KEEP.txt" 'same-named folder owned by another installed app'
        Kept $oKey "the other app's Installed-apps entry"
    }
    else { Write-Host "`n== Machine-wide scenario skipped (needs admin)" }
}
finally {
    foreach ($p in $lockers) { try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { } }
    Start-Sleep 1
    foreach ($p in $cleanupPaths) {
        if (Test-Path -LiteralPath $p) { icacls "$p" /remove:d "*$sid" 2>$null | Out-Null }
        # Junctions/symlinks first, so cleanup never walks into their targets.
        Get-ChildItem -LiteralPath $p -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue |
            ForEach-Object { cmd /c rmdir "$($_.FullName)" 2>$null }
        if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { cmd /c rmdir "$p" 2>$null }
        else { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue }
    }
    foreach ($k in $cleanupKeys) { Remove-Item -LiteralPath $k -Recurse -Force -ErrorAction SilentlyContinue }
    foreach ($v in 'ZephyrPaintPro', 'ZephyrBackupDemo') { Remove-ItemProperty $runKey -Name $v -ErrorAction SilentlyContinue }
    Get-ChildItem $env:TEMP -Directory -Filter 'zephyr-un-*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem "$([Environment]::GetFolderPath('MyDocuments'))\Untrail Backups" -Directory -Filter 'Zephyr*' -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Remove-OwnRecycled '*\Zephyr Recycle Demo*'
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "All checks passed" -ForegroundColor Green
