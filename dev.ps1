param(
  [switch]$Admin
)

Write-Host "Starting dev with Hot Reload..." -ForegroundColor Cyan

if ($Admin -and -not ([bool]([Security.Principal.WindowsIdentity]::GetCurrent()).Groups -match 'S-1-5-32-544')) {
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = "powershell.exe"
  $args = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
  $psi.Arguments = $args
  $psi.Verb = "runas"
  try { [Diagnostics.Process]::Start($psi) | Out-Null } catch { Write-Error $_ }
  exit
}

$env:DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER = "1"
dotnet watch run --project "OverlayOverlay/OverlayOverlay.csproj" -c Debug

