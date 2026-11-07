# fixture.ps1 - the "window of ANOTHER process" for the click-through test: a plain opaque WinForms
# form that logs every mouse message it receives (screen coordinates, unix ms) to -Log.
#   pwsh -NoProfile -File fixture.ps1 -Log <file> -X 560 -Y 260 -W 120 -H 120
param([Parameter(Mandatory)] [string]$Log, [int]$X = 560, [int]$Y = 260, [int]$W = 120, [int]$H = 120)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
# Hide this pwsh's own console window (it would sit over the test area). Done here, not with
# Start-Process -WindowStyle Hidden: that hint applies to the process's FIRST ShowWindow, which
# would be the form's.
Add-Type -Namespace PopupFixture -Name Con -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern System.IntPtr GetConsoleWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int cmd);
'@
$cw = [PopupFixture.Con]::GetConsoleWindow()
if ($cw -ne [IntPtr]::Zero) { [PopupFixture.Con]::ShowWindow($cw, 0) | Out-Null }
$f = [System.Windows.Forms.Form]::new()
$f.FormBorderStyle = 'None'
$f.StartPosition = 'Manual'
$f.Location = [System.Drawing.Point]::new($X, $Y)
$f.Size = [System.Drawing.Size]::new($W, $H)
$f.BackColor = [System.Drawing.Color]::FromArgb(90, 90, 90)
$f.ShowInTaskbar = $false
$f.Text = 'popup-fixture'
$script:sw = [System.IO.StreamWriter]::new($Log, $true)
$script:sw.AutoFlush = $true
function Write-Ev([string]$kind, $e) {
  $p = $f.PointToScreen($e.Location)
  $script:sw.WriteLine("$kind $($p.X) $($p.Y) $([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())")
}
$f.Add_MouseDown({ param($s, $e) Write-Ev 'down' $e })
$f.Add_MouseUp({ param($s, $e) Write-Ev 'up' $e })
$f.Add_MouseMove({ param($s, $e) Write-Ev 'move' $e })
$f.Add_Shown({ $script:sw.WriteLine("shown $($f.Location.X) $($f.Location.Y) $PID") })
[System.Windows.Forms.Application]::Run($f)
