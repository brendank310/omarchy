# Exercise the real install script with isolated files and mocked Windows
# driver/service operations. This does not test PnPUtil, SCM, or Windows ACLs.
$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('enhanced-installer-' + [Guid]::NewGuid().ToString('N'))
$oldProgramFiles = $env:ProgramFiles
$driveName = 'EWTest' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
New-Item -ItemType Directory -Path $scratch | Out-Null
$env:ProgramFiles = Join-Path $scratch 'ProgramFiles'
$payload = Join-Path $scratch 'payload'
$media = Join-Path $scratch 'media'
New-Item -ItemType Directory -Path $payload, $media | Out-Null
New-PSDrive -Name $driveName -PSProvider FileSystem -Root $media | Out-Null
$exeName = 'Omarchy.EnhancedWindows.exe'
Set-Content -LiteralPath (Join-Path $payload $exeName) -Value 'new executable'
$driverDirectory = Join-Path $media 'vioserial/w11/amd64'
New-Item -ItemType Directory -Path $driverDirectory | Out-Null
Set-Content -LiteralPath (Join-Path $driverDirectory 'vioser.inf') -Value 'mock INF'
Set-Content -LiteralPath (Join-Path $driverDirectory 'vioser.cat') -Value 'mock catalog'
$iso = Join-Path $scratch 'drivers.iso'
Set-Content -LiteralPath $iso -Value 'mock ISO'
$digest = (Get-FileHash -LiteralPath $iso -Algorithm SHA256).Hash
$scriptCopy = Join-Path $scratch 'Install.ps1'
# Only the isolated copy omits the elevation directive; every external
# privileged operation below is mocked. Production retains #Requires.
(Get-Content -LiteralPath "$package/Install.ps1" -Raw).Replace('#Requires -RunAsAdministrator', '# Isolated installer test') |
  Set-Content -LiteralPath $scriptCopy
$installed = Join-Path $env:ProgramFiles 'Omarchy/Enhanced Windows/Omarchy.EnhancedWindows.exe'
$expectedBinary = "`"$env:ProgramFiles\Omarchy\Enhanced Windows\$exeName`" service"

function Get-DiskImage { param($ImagePath) [pscustomobject]@{ Attached = $true } }
function Get-Volume {
  param([Parameter(ValueFromPipeline)]$Image)
  process { [pscustomobject]@{ DriveLetter = $driveName } }
}
function Get-AuthenticodeSignature {
  param($LiteralPath)
  [pscustomobject]@{ Status = $global:EnhancedInstallerTestState.Signature }
}
function Start-Process {
  param($FilePath, $ArgumentList, [switch]$Wait, [switch]$PassThru, [switch]$NoNewWindow)
  if ($FilePath -ne 'pnputil.exe' -or $ArgumentList[0] -ne '/add-driver' -or $ArgumentList[2] -ne '/install') { throw 'Unexpected process invocation' }
  $global:EnhancedInstallerTestState.DriverCalls++
  [pscustomobject]@{ ExitCode = $global:EnhancedInstallerTestState.DriverExit }
}
function Get-Service {
  [CmdletBinding()] param($Name)
  if ($global:EnhancedInstallerTestState.Exists) {
    [pscustomobject]@{ Status = $global:EnhancedInstallerTestState.Status }
  }
}
function Get-CimInstance {
  param($ClassName, $Filter)
  [pscustomobject]@{ PathName = $expectedBinary; StartName = 'LocalSystem' }
}
function Stop-Service {
  [CmdletBinding()] param($Name, [switch]$Force)
  $global:EnhancedInstallerTestState.Status = 'Stopped'
}
function Start-Service {
  [CmdletBinding()] param($Name)
  $global:EnhancedInstallerTestState.StartCalls++
  if ($global:EnhancedInstallerTestState.FailStart -and $global:EnhancedInstallerTestState.StartCalls -eq 1) {
    throw 'Injected service startup failure'
  }
  $global:EnhancedInstallerTestState.Status = 'Running'
}
function New-Service {
  param($Name, $BinaryPathName, $StartupType, $DisplayName, $Description)
  $global:EnhancedInstallerTestState.Exists = $true
}
function icacls.exe { $global:LASTEXITCODE = 0 }
function sc.exe {
  if ($args[0] -eq 'delete') { $global:EnhancedInstallerTestState.Exists = $false }
  $global:LASTEXITCODE = 0
}
function Assert($condition, $description) {
  if (-not $condition) { throw $description }
  Write-Output "ok - $description"
}

try {
  foreach ($case in @('fresh', 'upgrade', 'driver-current', 'driver-reboot', 'driver-failure', 'failed-upgrade', 'failed-fresh', 'bad-digest', 'bad-signature')) {
    if (Test-Path -LiteralPath $env:ProgramFiles) { Remove-Item -LiteralPath $env:ProgramFiles -Recurse -Force }
    $upgrade = $case -in @('upgrade', 'failed-upgrade')
    $global:EnhancedInstallerTestState = @{
      DriverExit = switch ($case) { 'driver-current' { 259 } 'driver-reboot' { 3010 } 'driver-failure' { 5 } default { 0 } }
      Exists = $upgrade; Status = 'Running'; DriverCalls = 0; StartCalls = 0
      FailStart = $case -in @('failed-upgrade', 'failed-fresh')
      Signature = if ($case -eq 'bad-signature') { 'NotSigned' } else { 'Valid' }
    }
    if ($upgrade) {
      New-Item -ItemType Directory -Path (Split-Path -Parent $installed) -Force | Out-Null
      Set-Content -LiteralPath $installed -Value 'old executable'
    }
    $failed = $false
    try {
      $hash = if ($case -eq 'bad-digest') { '0' * 64 } else { $digest }
      & $scriptCopy -Payload $payload -VirtioIso $iso -VirtioSha256 $hash | Out-Null
    } catch { $failed = $true }
    switch ($case) {
      { $_ -in @('fresh', 'upgrade', 'driver-current', 'driver-reboot') } {
        Assert (-not $failed) "$case succeeds"
        Assert ((Get-Content -LiteralPath $installed -Raw).Trim() -eq 'new executable') "$case installs the new binary"
        Assert ($global:EnhancedInstallerTestState.Status -eq 'Running') "$case starts the service"
      }
      'failed-upgrade' {
        Assert $failed 'Failed upgrade reports an error'
        Assert ((Get-Content -LiteralPath $installed -Raw).Trim() -eq 'old executable') 'Failed upgrade restores the previous binary'
        Assert ($global:EnhancedInstallerTestState.Status -eq 'Running') 'Failed upgrade restarts the previous service'
      }
      'failed-fresh' {
        Assert $failed 'Failed fresh installation reports an error'
        Assert (-not (Test-Path -LiteralPath $installed)) 'Failed fresh installation removes its executable'
        Assert (-not $global:EnhancedInstallerTestState.Exists) 'Failed fresh installation removes its service'
      }
      'driver-failure' {
        Assert $failed 'Driver installation error is rejected'
        Assert (-not $global:EnhancedInstallerTestState.Exists) 'Driver error prevents service installation'
      }
      'bad-digest' {
        Assert $failed 'Invalid driver digest is rejected'
        Assert ($global:EnhancedInstallerTestState.DriverCalls -eq 0) 'Digest mismatch prevents driver execution'
      }
      'bad-signature' {
        Assert $failed 'Unsigned driver catalog is rejected'
        Assert ($global:EnhancedInstallerTestState.DriverCalls -eq 0) 'Invalid catalog signature prevents driver execution'
      }
    }
  }
} finally {
  $env:ProgramFiles = $oldProgramFiles
  Remove-PSDrive -Name $driveName
  Remove-Item -LiteralPath $scratch -Recurse -Force
  Remove-Variable -Name EnhancedInstallerTestState -Scope Global
}
