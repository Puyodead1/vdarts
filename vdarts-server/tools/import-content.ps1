<#
.SYNOPSIS
  Fill vdarts-server's content directory (mall catalogue, backgrounds, previews, ads) from local data.

.DESCRIPTION
  Copies ONLY whitelisted, non-personal files:
    -Archive  <dir>   archive of the official server (vdarts-net-YYYY-MM-DD):
                        api\fxPersonal_all_<lang>.json -> fx\
                        api\fxGetAllBak.json           -> fx\
                        fx\**\*.jpg|png                -> fx\<same sub-path>   (previews, if the archive has them)
    -Cabinet  <dir>   an extracted cabinet data folder (…\VDartsH2_V1):
                        GameData\fx\bak\*.jpg          -> fx\bak\
                        Ads\pic\*.jpg|jpeg|png         -> ads\pic\
                        Ads\video\*.ogv                -> ads\video\
  Nothing else is read. In particular GameData\*.dat, prefs*, Player.log and setting.txt are never copied:
  they hold the original cabinet's card numbers and machine identity.

.EXAMPLE
  .\import-content.ps1 -Archive D:\vdarts\archive\vdarts-net-2026-11-17 -Cabinet D:\vdarts\cabinet\VDartsH2_V1 -Content D:\vdarts-server-bin\data\content
#>
param(
  [string]$Archive = "",
  [string]$Cabinet = "",
  [Parameter(Mandatory = $true)][string]$Content
)
$ErrorActionPreference = "Stop"

function Copy-Matching([string]$from, [string]$to, [string[]]$patterns) {
  if (-not (Test-Path $from)) { Write-Host "  (missing) $from"; return 0 }
  New-Item -ItemType Directory -Force $to | Out-Null
  $n = 0
  foreach ($p in $patterns) {
    foreach ($f in Get-ChildItem -LiteralPath $from -Filter $p -File) {
      Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $to $f.Name) -Force; $n++
    }
  }
  Write-Host ("  {0,4} files  {1} -> {2}" -f $n, $from, $to)
  return $n
}

New-Item -ItemType Directory -Force $Content | Out-Null
$Content = (Resolve-Path $Content).Path

if ($Archive -ne "") {
  Write-Host "Archive: $Archive"
  Copy-Matching (Join-Path $Archive "api") (Join-Path $Content "fx") @("fxPersonal_all_*.json", "fxGetAllBak.json") | Out-Null
  $fx = Join-Path $Archive "fx"
  if (Test-Path $fx) {
    foreach ($d in @($fx) + @(Get-ChildItem -LiteralPath $fx -Directory -Recurse | ForEach-Object FullName)) {
      $rel = $d.Substring($fx.Length).TrimStart('\', '/')
      Copy-Matching $d (Join-Path (Join-Path $Content "fx") $rel) @("*.jpg", "*.jpeg", "*.png") | Out-Null
    }
  } else { Write-Host "  (no fx\ previews in the archive yet)" }
}

if ($Cabinet -ne "") {
  Write-Host "Cabinet: $Cabinet"
  Copy-Matching (Join-Path $Cabinet "GameData\fx\bak") (Join-Path $Content "fx\bak") @("*.jpg") | Out-Null
  Copy-Matching (Join-Path $Cabinet "Ads\pic") (Join-Path $Content "ads\pic") @("*.jpg", "*.jpeg", "*.png") | Out-Null
  Copy-Matching (Join-Path $Cabinet "Ads\video") (Join-Path $Content "ads\video") @("*.ogv") | Out-Null
}
Write-Host "Done. Restart vdarts-server (or point --Vdarts:ContentDir at $Content)."
