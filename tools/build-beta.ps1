<#
    build-beta.ps1 - build the next beta of the launcher.

    Every beta that gets installed has its own number, in the third decimal
    place: after the v0.93 release they are 0.93.1, 0.93.2, ... This bumps <BetaBuild> in the project file,
    then builds to bin\Release\net48. A plain compile check (dotnet build -o
    somewhere else) does not bump anything.

        tools\build-beta.ps1            next beta number, then build
        tools\build-beta.ps1 -NoBump    rebuild under the current number

    Close the running launcher first - its exe is locked while it runs.
#>
[CmdletBinding()]
param([switch] $NoBump)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'ABeautifulPotatoLauncher.csproj'
$text = [IO.File]::ReadAllText($proj)

if ($text -notmatch '<Beta>true</Beta>') { throw 'Beta is false in the project file - this is a release build, not a beta.' }
$m = [regex]::Match($text, '<BetaBuild>(\d+)</BetaBuild>')
if (-not $m.Success) { throw 'No <BetaBuild> in the project file.' }
$base = [regex]::Match($text, '<VersionBase>([\d.]+)</VersionBase>').Groups[1].Value

$num = [int]$m.Groups[1].Value
if (-not $NoBump) {
    $num++
    $text = $text.Substring(0, $m.Index) + "<BetaBuild>$num</BetaBuild>" + $text.Substring($m.Index + $m.Length)
    [IO.File]::WriteAllText($proj, $text)
}
Write-Host "building Beta v$base.$num"

dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

$exe = Join-Path $root 'bin\Release\net48\ABeautifulPotatoLauncher.exe'
Write-Host ("built " + (Get-Item $exe).VersionInfo.ProductVersion)
