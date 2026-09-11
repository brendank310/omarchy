#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$name = 'OmarchyEnhancedWindows'
if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
  Stop-Service -Name $name -Force
  & sc.exe delete $name | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Could not delete service' }
}
$target = "$env:ProgramFiles\Omarchy\Enhanced Windows"
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
# Keep VirtIO drivers: the guest's boot disk and network may depend on them.
