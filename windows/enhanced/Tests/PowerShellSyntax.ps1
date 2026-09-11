$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $PSScriptRoot
foreach ($script in Get-ChildItem -LiteralPath $package -Filter '*.ps1' -Recurse) {
  if ($script.FullName -match '[\\/]artifacts[\\/]') { continue }
  $tokens = $null
  $errors = $null
  [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
  if ($errors.Count -ne 0) { throw ($errors | Out-String) }
  Write-Output "ok - PowerShell syntax: $($script.Name)"
}
