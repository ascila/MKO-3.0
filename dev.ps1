param(
  [switch]$Admin
)

Write-Host "Starting dev with Hot Reload..." -ForegroundColor Cyan

# Load .env if present (safe local dev only)
$dotenv = Join-Path $PSScriptRoot ".env"
if (Test-Path $dotenv) {
  Write-Host "Loading .env" -ForegroundColor DarkCyan
  Get-Content $dotenv | ForEach-Object {
    $line = $_.Trim()
    if ([string]::IsNullOrWhiteSpace($line)) { return }
    if ($line.StartsWith('#')) { return }
    $kv = $line -split '=', 2
    if ($kv.Length -eq 2) {
      $k = $kv[0].Trim()
      $v = $kv[1].Trim().Trim('"')
      if (-not [string]::IsNullOrWhiteSpace($k)) {
        [System.Environment]::SetEnvironmentVariable($k, $v, 'Process')
      }
    }
  }
}

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
