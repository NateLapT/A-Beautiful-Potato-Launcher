<#
    ui-inject.ps1 - guarded UI automation for testing the launcher.

    WHY THIS EXISTS
      Naive automation sent a right-click into VS Code and typed "motox" into a
      Discord message box. The cause was not the clicking - it was injecting
      WITHOUT CHECKING WHERE THE INPUT WOULD LAND. SetForegroundWindow returns
      true but silently does nothing when called from a background process, so
      the input went to whatever was actually on top.

    THE THREE GUARDS
      1. IDLE CHECK   - GetLastInputInfo says how long since the user last
                        touched the machine. If they are active, ask first.
      2. WARNING      - a visible, on-top dialog before anything is injected.
                        Active user: must consent, and a timeout means NO.
                        Idle user: brief notice, auto-continues.
      3. FOREGROUND   - immediately before EVERY event, confirm the target
                        window really is foreground. If it is not, abort rather
                        than inject blind. This is the guard that actually
                        prevents stray input.

    USAGE
      ui-inject.ps1 -Proc BeautifulPotatoExpLauncher -Click 400,113
      ui-inject.ps1 -Proc BeautifulPotatoExpLauncher -RightClick 400,113
      ui-inject.ps1 -Proc BeautifulPotatoExpLauncher -Keys "^f" -Then "motox"
      ui-inject.ps1 -Proc X -Click 10,10 -Shot out.png
      Add -Force to skip the prompt (still enforces the foreground check).
#>

param(
    [Parameter(Mandatory = $true)][string]$Proc,
    [int[]]$Click,
    [int[]]$RightClick,
    [string]$Keys,
    [string]$Then,
    [string]$Shot,
    [int]$WaitMs = 0,
    [int]$IdleSeconds = 45,
    [switch]$Force
)

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;

public class UiGuard {
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    // Without this the process is DPI-virtualised: GetWindowRect and
    // SetCursorPos then disagree the moment a window sits on a monitor with a
    // different scale factor, and clicks land somewhere else entirely.
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
    [DllImport("kernel32.dll")] public static extern uint GetTickCount();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);

    public const uint LDOWN = 0x0002, LUP = 0x0004, RDOWN = 0x0008, RUP = 0x0010;

    /// <summary>Seconds since the user last touched keyboard or mouse.</summary>
    public static double IdleSeconds() {
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        if (!GetLastInputInfo(ref info)) return 0;
        return (GetTickCount() - info.dwTime) / 1000.0;
    }
}
'@

# Must happen before any window geometry is read.
[void][UiGuard]::SetProcessDPIAware()

function Get-Target {
    $p = Get-Process -Name $Proc -ErrorAction SilentlyContinue |
         Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { throw "No window found for process '$Proc'." }
    return $p.MainWindowHandle
}

# --- guard 3: never inject unless the target really has the foreground -------
function Assert-Foreground($h) {
    if ([UiGuard]::IsIconic($h)) { [void][UiGuard]::ShowWindow($h, 9) }  # SW_RESTORE
    [void][UiGuard]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 350

    $fg = [UiGuard]::GetForegroundWindow()
    if ($fg -ne $h) {
        throw ("ABORTED: '$Proc' is not the foreground window (foreground=$fg, target=$h). " +
               "Injecting now would send input to another application. " +
               "Click the launcher window once, then re-run.")
    }
}

# --- guards 1 and 2: idle check, then a visible warning ---------------------
function Confirm-Injection {
    $idle = [UiGuard]::IdleSeconds()
    $active = $idle -lt $IdleSeconds
    Write-Host ("user idle {0:N1}s -> {1}" -f $idle, $(if ($active) { "ACTIVE" } else { "away" }))

    if ($Force) { Write-Host "  -Force given, skipping the prompt"; return $true }

    $what = @()
    if ($Click)      { $what += "left-click at $($Click -join ',')" }
    if ($RightClick) { $what += "right-click at $($RightClick -join ',')" }
    if ($Keys)       { $what += "keys '$Keys'" }
    if ($Then)       { $what += "text '$Then'" }
    $summary = if ($what.Count) { $what -join ", " } else { "no input" }

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Claude wants to control the mouse/keyboard"
    $form.Size = New-Object System.Drawing.Size(520, 240)
    $form.StartPosition = "CenterScreen"
    $form.TopMost = $true
    $form.FormBorderStyle = "FixedDialog"
    $form.BackColor = [System.Drawing.Color]::FromArgb(28, 28, 30)
    $form.ForeColor = [System.Drawing.Color]::Gainsboro

    $msg = New-Object System.Windows.Forms.Label
    $msg.Location = New-Object System.Drawing.Point(16, 16)
    $msg.Size = New-Object System.Drawing.Size(480, 110)
    $msg.Text = if ($active) {
        "You appear to be USING this machine (idle $([math]::Round($idle,1))s).`r`n`r`n" +
        "About to send: $summary`r`nTarget window: $Proc`r`n`r`n" +
        "Input will only be sent while that window is in front. Continue?`r`n" +
        "(No response = Cancel)"
    } else {
        "You appear to be away (idle $([math]::Round($idle,1))s).`r`n`r`n" +
        "About to send: $summary`r`nTarget window: $Proc`r`n`r`nContinuing automatically..."
    }
    $form.Controls.Add($msg)

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = "Allow"; $ok.Location = New-Object System.Drawing.Point(280, 150)
    $ok.Size = New-Object System.Drawing.Size(100, 32)
    $ok.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $ok.BackColor = [System.Drawing.Color]::FromArgb(70, 110, 70)
    $ok.FlatStyle = "Flat"; $ok.ForeColor = [System.Drawing.Color]::White
    $form.Controls.Add($ok)

    $no = New-Object System.Windows.Forms.Button
    $no.Text = "Cancel"; $no.Location = New-Object System.Drawing.Point(390, 150)
    $no.Size = New-Object System.Drawing.Size(100, 32)
    $no.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $no.BackColor = [System.Drawing.Color]::FromArgb(70, 55, 55)
    $no.FlatStyle = "Flat"; $no.ForeColor = [System.Drawing.Color]::White
    $form.Controls.Add($no)
    $form.AcceptButton = $ok; $form.CancelButton = $no

    # Active user must consent and a timeout means no; an away user just gets
    # a few seconds of notice.
    $timeout = if ($active) { 20 } else { 3 }
    $result = if ($active) { [System.Windows.Forms.DialogResult]::Cancel }
              else { [System.Windows.Forms.DialogResult]::OK }

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = $timeout * 1000
    $timer.Add_Tick({ $form.DialogResult = $result; $form.Close() })
    $timer.Start()

    $answer = $form.ShowDialog()
    $timer.Stop(); $form.Dispose()

    if ($answer -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-Host "  DECLINED - nothing was injected"
        return $false
    }
    Write-Host "  allowed"
    return $true
}

# ----------------------------------------------------------------- run -----
$h = Get-Target

# Compare explicitly against $true: anything else - $false, $null, or a stray
# object - refuses to inject. Fail closed.
$consent = Confirm-Injection
if ($consent -ne $true) {
    Write-Output "INJECTION REFUSED"
    exit 2
}

$r = New-Object UiGuard+RECT
[void][UiGuard]::GetWindowRect($h, [ref]$r)
Write-Output ("target at {0},{1} size {2}x{3}" -f $r.L, $r.T, ($r.R - $r.L), ($r.B - $r.T))

if ($Click) {
    Assert-Foreground $h
    [void][UiGuard]::SetCursorPos(($r.L + $Click[0]), ($r.T + $Click[1]))
    Start-Sleep -Milliseconds 120
    Assert-Foreground $h                       # re-check: nothing stole focus
    [UiGuard]::mouse_event([UiGuard]::LDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [UiGuard]::mouse_event([UiGuard]::LUP, 0, 0, 0, [IntPtr]::Zero)
    Write-Output ("  left-clicked " + ($Click -join ","))
    Start-Sleep -Milliseconds 350
}

if ($RightClick) {
    Assert-Foreground $h
    [void][UiGuard]::SetCursorPos(($r.L + $RightClick[0]), ($r.T + $RightClick[1]))
    Start-Sleep -Milliseconds 120
    Assert-Foreground $h
    [UiGuard]::mouse_event([UiGuard]::RDOWN, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [UiGuard]::mouse_event([UiGuard]::RUP, 0, 0, 0, [IntPtr]::Zero)
    Write-Output ("  right-clicked " + ($RightClick -join ","))
    Start-Sleep -Milliseconds 400
}

foreach ($text in @($Keys, $Then)) {
    if ([string]::IsNullOrEmpty($text)) { continue }
    Assert-Foreground $h
    [System.Windows.Forms.SendKeys]::SendWait($text)
    Write-Output "  sent '$text'"
    Start-Sleep -Milliseconds 300
}

if ($WaitMs -gt 0) { Start-Sleep -Milliseconds $WaitMs }

if ($Shot) {
    [void][UiGuard]::GetWindowRect($h, [ref]$r)
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    [void][UiGuard]::PrintWindow($h, $dc, 2)      # PW_RENDERFULLCONTENT
    $g.ReleaseHdc($dc); $g.Dispose()
    $bmp.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "  saved $Shot (${w}x${ht})"
}
