# Builds Untrail with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
#
#   ./build.ps1 -Version 1.0.0
#
# Output in dist\:
#   Untrail.exe                    the app (runs portable too)
#   UntrailSetup.exe               per-user installer with Untrail.exe inside
#   UntrailSetup.zip               the installer, INSTALL.txt and LICENSE (the website's download)
#   Untrail-<version>-portable.zip
#   SHA256SUMS.txt
#
# -Stage runs one step at a time, so a release can sign Untrail.exe before the installer embeds it:
#   app        clears the output folder and builds Untrail.exe
#   installer  builds UntrailSetup.exe around the Untrail.exe already in the output folder
#   package    makes the zips and SHA256SUMS.txt from what's in the output folder
#   all        all three (the default)
param(
    [string]$Version = "1.0.0",
    [string]$OutDir = "dist",
    [ValidateSet('all', 'app', 'installer', 'package')] [string]$Stage = 'all'
)
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)') { throw "Version must look like 1.2.3 (got '$Version')" }
$numeric = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

$root = $PSScriptRoot
$obj = Join-Path $root 'obj'
$dist = Join-Path $root $OutDir
New-Item -ItemType Directory -Force $obj, $dist | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

function Write-AssemblyInfo($file, $title) {
    @"
[assembly: System.Reflection.AssemblyTitle("$title")]
[assembly: System.Reflection.AssemblyProduct("Untrail")]
[assembly: System.Reflection.AssemblyCompany("Untrail")]
[assembly: System.Reflection.AssemblyDescription("$title")]
[assembly: System.Reflection.AssemblyCopyright("Untrail contributors")]
[assembly: System.Reflection.AssemblyVersion("$numeric")]
[assembly: System.Reflection.AssemblyFileVersion("$numeric")]
[assembly: System.Reflection.AssemblyInformationalVersion("$Version")]
namespace Untrail { static class BuildInfo { public const string Version = "$Version"; } }
"@ | Set-Content $file -Encoding UTF8
}

function Invoke-Csc($out, $sources, $resources) {
    $res = $resources | ForEach-Object { "/resource:$($_[0]),$($_[1])" }
    & $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
        /win32manifest:"$root\src\app.manifest" /win32icon:"$root\src\assets\untrail.ico" `
        /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.VisualBasic.dll /reference:System.Web.Extensions.dll `
        /out:$out @res @sources
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $out" }
}

$art = @(
    @("$root\src\assets\logo-dark.png", 'Untrail.logo-dark.png'),
    @("$root\src\assets\mark.png", 'Untrail.mark.png'),
    @("$root\src\assets\untrail.ico", 'Untrail.untrail.ico')
)
$shared = Get-ChildItem "$root\src\shared" -Filter *.cs | ForEach-Object { $_.FullName }

# 1. The app
if ($Stage -in 'all', 'app') {
    Remove-Item "$dist\*" -Force -ErrorAction SilentlyContinue
    Write-AssemblyInfo "$obj\AppInfo.g.cs" 'Untrail - uninstall apps completely'
    $appSources = @(Get-ChildItem "$root\src\app" -Filter *.cs | ForEach-Object { $_.FullName }) + $shared + "$obj\AppInfo.g.cs"
    Invoke-Csc "$dist\Untrail.exe" $appSources $art
}

# 2. The installer, carrying the app inside it
if ($Stage -in 'all', 'installer') {
    if (-not (Test-Path "$dist\Untrail.exe")) { throw "No $dist\Untrail.exe to put in the installer: run -Stage app first" }
    Write-AssemblyInfo "$obj\SetupInfo.g.cs" 'Untrail Setup'
    $setupSources = @(Get-ChildItem "$root\src\setup" -Filter *.cs | ForEach-Object { $_.FullName }) + $shared + "$obj\SetupInfo.g.cs"
    Invoke-Csc "$dist\UntrailSetup.exe" $setupSources ($art + , @("$dist\Untrail.exe", 'Untrail.payload.exe'))
}

# 3. Zips + checksums. UntrailSetup.zip keeps one name across releases, so releases/latest/download/UntrailSetup.zip always works.
if ($Stage -in 'all', 'package') {
    Remove-Item "$dist\*.zip", "$dist\SHA256SUMS.txt" -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path "$dist\UntrailSetup.exe", "$root\src\setup\INSTALL.txt", "$root\LICENSE" -DestinationPath "$dist\UntrailSetup.zip" -Force
    Compress-Archive -Path "$dist\Untrail.exe", "$root\README.md", "$root\LICENSE" -DestinationPath "$dist\Untrail-$Version-portable.zip" -Force
    Get-ChildItem $dist -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
        "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)"
    } | Set-Content "$dist\SHA256SUMS.txt" -Encoding ASCII
}

Get-ChildItem $dist | ForEach-Object { "{0,-34} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB) }
