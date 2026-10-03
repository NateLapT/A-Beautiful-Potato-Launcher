# ---------------------------------------------------------------------------
#  Removes the fake servers from assets/default-servers.tsv.gz.
#
#  Run by tools/build-default-servers.py after it has written the raw list.
#
#  THE LAUNCHER'S OWN RULES, NOT A COPY. This loads the built launcher and runs
#  MainForm.RecomputeFarms / FakeReason on the list - the exact code the browser
#  uses to hide fakes - so the shipped list and the screen can never disagree.
#  A re-implementation here would drift the first time a threshold changed.
#
#  MainForm is created without running its constructor (no window, no Steam),
#  so its field initialisers never ran: every empty collection field is
#  created here instead, with the case-insensitive comparer the class uses.
#
#  The farm scan runs twice, as it does in the live browser: the "most of this
#  address is fake" rule reads the PREVIOUS pass's verdicts by design.
#
#  Not applied: the identical-mod-list rule. It needs every server's mod list,
#  which the source does not carry. Everything else is.
#
#  Usage:  powershell -File tools\screen-default-servers.ps1 [-Exe <launcher exe>]
# ---------------------------------------------------------------------------

param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\bin\Debug\net48\ABeautifulPotatoLauncher.exe"),
    [string]$List = (Join-Path $PSScriptRoot "..\assets\default-servers.tsv.gz"),

    # Optional: where to write why each removed server WITH PLAYERS was
    # removed - the ones most likely to be real. Keep it local; the reasons
    # are a description of the rules (see .gitignore on serverdata).
    [string]$Report = ""
)

$ErrorActionPreference = "Stop"
$Exe = (Resolve-Path $Exe).Path
$List = (Resolve-Path $List).Path

# Loaded from a copy, so the build can overwrite the real exe while this runs.
$tmp = Join-Path $env:TEMP ("abpl-screen-" + [guid]::NewGuid().ToString("N") + ".exe")
Copy-Item $Exe $tmp
$asm = [Reflection.Assembly]::LoadFrom($tmp)

$flags = [Reflection.BindingFlags]"Instance,NonPublic,Public"
$mainType = $asm.GetType("ABeautifulPotatoLauncher.MainForm")
$serverType = $asm.GetType("ABeautifulPotatoLauncher.BrowserServer")
$form = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($mainType)

foreach ($f in $mainType.GetFields($flags)) {
    if ($null -ne $f.GetValue($form)) { continue }
    $t = $f.FieldType
    if ($t -eq [object]) { $f.SetValue($form, (New-Object object)); continue }
    if (-not $t.IsGenericType) { continue }
    $def = $t.GetGenericTypeDefinition()
    $args0 = $t.GetGenericArguments()[0]
    if (($def -eq [Collections.Generic.HashSet`1] -or $def -eq [Collections.Generic.Dictionary`2]) -and $args0 -eq [string]) {
        $f.SetValue($form, [Activator]::CreateInstance($t, [object[]]@([StringComparer]::OrdinalIgnoreCase)))
    }
    elseif ($def -eq [Collections.Generic.HashSet`1] -or $def -eq [Collections.Generic.Dictionary`2] -or $def -eq [Collections.Generic.List`1]) {
        $f.SetValue($form, [Activator]::CreateInstance($t))
    }
}

# ---- read ----
$lines = New-Object Collections.Generic.List[string]
$in = New-Object IO.Compression.GZipStream((New-Object IO.FileStream($List, "Open", "Read")), [IO.Compression.CompressionMode]::Decompress)
$rd = New-Object IO.StreamReader($in, [Text.Encoding]::UTF8)
while ($null -ne ($l = $rd.ReadLine())) { if ($l.Length -gt 0) { $lines.Add($l) } }
$rd.Close()

$listType = [Collections.Generic.List``1].MakeGenericType($serverType)
$cache = [Activator]::CreateInstance($listType)
$fld = @{}
foreach ($n in "Name","Map","GameDir","Tags","Host","Port","QueryPort","Players","MaxPlayers","Ping","AppId","Password","Secure") {
    $fld[$n] = $serverType.GetField($n)
}
foreach ($l in $lines) {
    $p = $l.Split("`t")
    $s = [Activator]::CreateInstance($serverType)
    $fld.Name.SetValue($s, $p[0]); $fld.Map.SetValue($s, $p[1]); $fld.GameDir.SetValue($s, $p[2])
    $fld.Tags.SetValue($s, $p[3]); $fld.Host.SetValue($s, $p[4])
    $fld.Port.SetValue($s, [int]$p[5]); $fld.QueryPort.SetValue($s, [int]$p[6])
    $fld.Players.SetValue($s, [int]$p[7]); $fld.MaxPlayers.SetValue($s, [int]$p[8])
    $fld.Ping.SetValue($s, [int]$p[9]); $fld.AppId.SetValue($s, [uint32]$p[10])
    $fld.Password.SetValue($s, $p[11] -eq "1"); $fld.Secure.SetValue($s, $p[12] -eq "1")
    $cache.Add($s)
}

# ---- screen ----
$recompute = $mainType.GetMethod("RecomputeFarms", $flags)
$fakeReason = $mainType.GetMethod("FakeReason", $flags)
$recompute.Invoke($form, @(,$cache)) | Out-Null
$recompute.Invoke($form, @(,$cache)) | Out-Null     # second pass: see header

$keep = New-Object Collections.Generic.List[string]
$why = New-Object Collections.Generic.List[string]
$dropped = 0
for ($i = 0; $i -lt $cache.Count; $i++) {
    $reason = $fakeReason.Invoke($form, @($cache[$i]))
    if ($null -eq $reason) { $keep.Add($lines[$i]); continue }
    $dropped++
    $p = $lines[$i].Split("`t")
    if ([int]$p[7] -gt 0) { $why.Add(("{0,3}/{1,-3} {2,-16} {3}  <=  {4}" -f $p[7], $p[8], $p[4], $p[0], $reason)) }
}
if ($Report) { $why | Set-Content -Path $Report -Encoding UTF8 }

# ---- write ----
$out = New-Object IO.Compression.GZipStream((New-Object IO.FileStream($List, "Create", "Write")), [IO.Compression.CompressionLevel]::Optimal)
$wr = New-Object IO.StreamWriter($out, (New-Object Text.UTF8Encoding($false)))
$wr.NewLine = "`n"
foreach ($l in $keep) { $wr.WriteLine($l) }
$wr.Close()

"Screened with the launcher's own rules: kept {0:N0}, removed {1:N0} fakes, of {2:N0}.  {3:N1} MB" -f `
    $keep.Count, $dropped, $lines.Count, ((Get-Item $List).Length / 1MB)
