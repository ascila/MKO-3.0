param(
  [string]$Desc = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
  try {
    $out = & git rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -eq 0 -and $out) { return (Resolve-Path $out).Path }
  } catch {}
  # Fallback: assume script is under <root>\tools
  return (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Sanitize-Name([string]$text, [int]$maxLen = 40) {
  if ([string]::IsNullOrWhiteSpace($text)) { return '' }
  $invalid = [System.IO.Path]::GetInvalidFileNameChars() -join ''
  $text = $text.Trim()
  $text = ($text -replace "[$invalid]", ' ')
  $text = ($text -replace '\s+', '-').Trim('-')
  if ($text.Length -gt $maxLen) { $text = $text.Substring(0, $maxLen).Trim('-') }
  return $text.ToLowerInvariant()
}

try {
  $root = Get-RepoRoot
  Set-Location $root

  if (-not (Test-Path '.git')) {
    throw "No Git repository found at: $root"
  }

  # Default description from last commit subject if not provided
  if ([string]::IsNullOrWhiteSpace($Desc)) {
    try {
      $Desc = (& git log -1 --pretty=%s 2>$null)
    } catch { $Desc = '' }
  }
  $san = Sanitize-Name $Desc

  $ts = Get-Date -Format 'yyyyMMdd-HHmmss'
  $sha = ''
  try { $sha = (& git rev-parse --short HEAD 2>$null) } catch { $sha = '' }
  $sha = ($sha ?? '').Trim()

  $nameParts = @($ts)
  if ($sha) { $nameParts += $sha }
  if ($san) { $nameParts += $san }
  $fileName = ($nameParts -join '-') + '.zip'

  $backupDir = Join-Path $root 'backups'
  if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir | Out-Null }
  $dest = Join-Path $backupDir $fileName

  Write-Host "[snapshot] Creating archive: $dest" -ForegroundColor Cyan
  # Use git archive to include only committed, tracked files.
  & git archive --format=zip -o "$dest" HEAD
  if ($LASTEXITCODE -ne 0) { throw "git archive failed with exit code $LASTEXITCODE" }

  Write-Host "[snapshot] Done." -ForegroundColor Green
  Write-Output $dest
} catch {
  Write-Error $_
  exit 1
}

