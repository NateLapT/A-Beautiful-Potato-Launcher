# ---------------------------------------------------------------------------
#  Builds the IP -> country table the launcher ships.
#
#  The five regional internet registries publish, every day, a free list of
#  which IPv4 ranges belong to which country. No API key and no third-party
#  service: the registries ARE the authority, and this reads them directly.
#
#  27 MB of text goes in; a 1.4 MB packed table comes out, which is embedded in
#  the executable. Run this by hand when the table starts feeling stale - a
#  handful of times a year is plenty, since allocations move slowly.
#
#      pwsh tools/build-ip-table.ps1
#
#  Output: assets/ip-country.bin
#
#  Format - everything little-endian, records sorted by Start so the launcher
#  can binary-search them:
#      magic    4 bytes  "IPC1"
#      count    4 bytes  number of records
#      records  10 bytes each: Start (uint32), Count (uint32), Country (2 ASCII)
# ---------------------------------------------------------------------------

[CmdletBinding()]
param(
    # Skip downloading and reuse whatever is already in the cache folder.
    [switch] $Offline,

    # Resolved below rather than here: $PSScriptRoot is not reliably populated
    # while parameter defaults are being evaluated under Windows PowerShell.
    [string] $CacheDir,
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
if (-not $here) { $here = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $here) { $here = (Get-Location).Path }

if (-not $CacheDir) { $CacheDir = Join-Path $here 'ip-cache' }
if (-not $OutFile)  { $OutFile  = Join-Path $here '..\assets\ip-country.bin' }

# RIPE mirrors all five registries, so one host serves the lot.
$sources = [ordered]@{
    arin    = 'https://ftp.ripe.net/pub/stats/arin/delegated-arin-extended-latest'
    ripencc = 'https://ftp.ripe.net/pub/stats/ripencc/delegated-ripencc-latest'
    apnic   = 'https://ftp.ripe.net/pub/stats/apnic/delegated-apnic-latest'
    lacnic  = 'https://ftp.ripe.net/pub/stats/lacnic/delegated-lacnic-latest'
    afrinic = 'https://ftp.ripe.net/pub/stats/afrinic/delegated-afrinic-latest'
}

New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null

foreach ($name in $sources.Keys) {
    $path = Join-Path $CacheDir "$name.txt"

    if ($Offline) {
        if (-not (Test-Path $path)) { throw "-Offline was given but $path is missing." }
        Write-Host ("  {0,-9} using cached copy" -f $name)
        continue
    }

    Write-Host ("  {0,-9} downloading..." -f $name) -NoNewline
    Invoke-WebRequest -Uri $sources[$name] -OutFile $path -UseBasicParsing
    $mb = [math]::Round((Get-Item $path).Length / 1MB, 1)
    Write-Host (" {0} MB" -f $mb)
}

# ---- parse -----------------------------------------------------------------
#
# Each line is pipe separated:
#   registry|cc|type|start|value|date|status[|extensions]
# Only ipv4 rows that are actually allocated or assigned carry a real country.

Write-Host 'Parsing...'
$records = New-Object 'System.Collections.Generic.List[object]'

foreach ($name in $sources.Keys) {
    foreach ($line in [System.IO.File]::ReadLines((Join-Path $CacheDir "$name.txt"))) {
        if ($line.Length -eq 0 -or $line[0] -eq '#') { continue }

        $f = $line.Split('|')
        if ($f.Length -lt 7)       { continue }
        if ($f[2] -ne 'ipv4')      { continue }
        if ($f[6] -ne 'allocated' -and $f[6] -ne 'assigned') { continue }

        $cc = $f[1].Trim()
        if ($cc.Length -ne 2) { continue }

        $octets = $f[3].Split('.')
        if ($octets.Length -ne 4) { continue }

        try {
            $start = ([uint32]$octets[0] -shl 24) -bor ([uint32]$octets[1] -shl 16) -bor
                     ([uint32]$octets[2] -shl 8)  -bor  [uint32]$octets[3]
            $count = [uint32]$f[4]
        } catch { continue }

        $records.Add([pscustomobject]@{ Start = $start; Count = $count; Cc = $cc })
    }
}

Write-Host ("  {0:N0} IPv4 ranges with a country" -f $records.Count)

# ---- merge ------------------------------------------------------------------
#
# Allocations to one country are usually contiguous, so neighbours collapse.
# This is what takes the table from 2.5 MB to 1.4 MB and costs nothing: a
# merged range answers exactly the same question.

$sorted = $records | Sort-Object Start
$merged = New-Object 'System.Collections.Generic.List[object]'

foreach ($r in $sorted) {
    if ($merged.Count -gt 0) {
        $last = $merged[$merged.Count - 1]
        if ($last.Cc -eq $r.Cc -and ($last.Start + $last.Count) -eq $r.Start) {
            $last.Count = $last.Count + $r.Count
            continue
        }
    }
    $merged.Add([pscustomobject]@{ Start = $r.Start; Count = $r.Count; Cc = $r.Cc })
}

Write-Host ("  {0:N0} after merging adjacent same-country ranges" -f $merged.Count)

# ---- write ------------------------------------------------------------------

$outDir = Split-Path -Parent $OutFile
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$stream = [System.IO.File]::Create($OutFile)
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    $writer.Write([byte[]][char[]]'IPC1')
    $writer.Write([int]$merged.Count)

    foreach ($r in $merged) {
        $writer.Write([uint32]$r.Start)
        $writer.Write([uint32]$r.Count)
        $writer.Write([byte][char]$r.Cc[0])
        $writer.Write([byte][char]$r.Cc[1])
    }
} finally {
    $writer.Dispose()
    $stream.Dispose()
}

$size = [math]::Round((Get-Item $OutFile).Length / 1MB, 2)
Write-Host ("Wrote {0} - {1} MB, {2:N0} ranges, {3} countries" -f `
            $OutFile, $size, $merged.Count, ($merged.Cc | Sort-Object -Unique).Count)
