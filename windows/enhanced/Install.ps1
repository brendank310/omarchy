#Requires -RunAsAdministrator
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$VirtioIso,
  [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$VirtioSha256,
  [string]$Payload = "$PSScriptRoot\win-x64"
)
$ErrorActionPreference = 'Stop'
$serviceName = 'OmarchyEnhancedWindows'
$target = "$env:ProgramFiles\Omarchy\Enhanced Windows"
$exe = 'Omarchy.EnhancedWindows.exe'

if (-not [Environment]::Is64BitOperatingSystem) { throw 'Windows x64 is required' }
if (-not (Test-Path -LiteralPath "$Payload\$exe" -PathType Leaf)) { throw 'Build the Windows package first' }
$iso = (Resolve-Path -LiteralPath $VirtioIso).Path
if ((Get-FileHash -LiteralPath $iso -Algorithm SHA256).Hash -ne $VirtioSha256) {
  throw 'VirtIO ISO SHA256 mismatch'
}

# Use a supplied, verified upstream ISO; never silently download mutable driver
# executables or disable Windows driver signature enforcement.
$image = Get-DiskImage -ImagePath $iso
$mountedHere = -not $image.Attached
try {
  if ($mountedHere) { $image = Mount-DiskImage -ImagePath $iso -PassThru }
  $volume = $image | Get-Volume
  $media = "$($volume.DriveLetter):\"
  # Upstream's MSI/EXE wrappers are unsigned; the driver catalogs are signed.
  # Validate catalogs, then let Windows validate and install each INF package.
  # The ISO also includes a non-VirtIO SMBus package signed by an untrusted
  # development root. It is outside this integration's driver scope.
  $drivers = @(Get-ChildItem -LiteralPath $media -Filter '*.inf' -Recurse |
    Where-Object { $_.DirectoryName -match '[\\/]w11[\\/]amd64$' -and $_.Name -ne 'smbus.inf' })
  if (-not ($drivers | Where-Object Name -eq 'vioser.inf')) { throw 'Windows 11 x64 VirtIO serial driver is missing from ISO' }
  foreach ($directory in ($drivers.DirectoryName | Sort-Object -Unique)) {
    $catalogs = @(Get-ChildItem -LiteralPath $directory -Filter '*.cat')
    if ($catalogs.Count -eq 0) { throw "Driver catalog is missing: $directory" }
    foreach ($catalog in $catalogs) {
      $signature = Get-AuthenticodeSignature -LiteralPath $catalog.FullName
      if ($signature.Status -ne 'Valid') { throw "VirtIO driver catalog signature is invalid: $($catalog.Name) ($($signature.Status))" }
    }
  }
  $rebootRequired = $false
  foreach ($driver in $drivers) {
    $result = Start-Process pnputil.exe -ArgumentList @('/add-driver', "`"$($driver.FullName)`"", '/install') -Wait -PassThru -NoNewWindow
    # 259 means no matching device or an equal/better driver is already installed.
    if ($result.ExitCode -notin @(0, 259, 3010)) { throw "VirtIO driver installation failed: $($driver.Name) (exit $($result.ExitCode))" }
    $rebootRequired = $rebootRequired -or $result.ExitCode -eq 3010
  }
} finally {
  if ($mountedHere) { Dismount-DiskImage -ImagePath $iso | Out-Null }
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$binary = "`"$target\$exe`" service"
if ($existing) {
  $configuration = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
  if ($configuration.PathName -ne $binary -or $configuration.StartName -ne 'LocalSystem') {
    throw 'Existing service has a custom executable or account; refusing to overwrite it'
  }
}
New-Item -ItemType Directory -Path $target -Force | Out-Null
# Administrators and SYSTEM own the code; desktop users only read/execute it.
& icacls.exe $target /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not protect installation directory' }
$installedExe = Join-Path $target $exe
$transaction = [Guid]::NewGuid().ToString('N')
$stagedExe = Join-Path $target "$transaction.stage.exe"
$backupExe = Join-Path $target "$transaction.backup.exe"
$wasRunning = $existing -and $existing.Status -eq 'Running'
$hadBinary = Test-Path -LiteralPath $installedExe -PathType Leaf
$binaryChanged = $false
$createdService = $false
try {
  # The publish output is a self-contained executable. Stage it before stopping
  # the service, then atomically replace it while retaining a rollback copy.
  Copy-Item -LiteralPath "$Payload\$exe" -Destination $stagedExe
  if ($existing) { Stop-Service -Name $serviceName -Force }
  if ($hadBinary) {
    [IO.File]::Replace($stagedExe, $installedExe, $backupExe)
  } else {
    [IO.File]::Move($stagedExe, $installedExe)
  }
  $binaryChanged = $true
  if (-not $existing) {
    New-Service -Name $serviceName -BinaryPathName $binary -StartupType Automatic `
      -DisplayName 'Omarchy Enhanced Windows experience' `
      -Description 'Windows app launch and window metadata over a dedicated VirtIO channel.' | Out-Null
    $createdService = $true
  }
  & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery' }
  & sc.exe failureflag $serviceName 1 | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Could not enable recovery after a reported service error' }
  Start-Service -Name $serviceName
} catch {
  $installationError = $_
  try {
    if ($binaryChanged) {
      Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
      if ($createdService) {
        & sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not remove the failed new service' }
      }
      if ($hadBinary) {
        [IO.File]::Replace($backupExe, $installedExe, [NullString]::Value)
      } else {
        Remove-Item -LiteralPath $installedExe -Force
      }
    }
    if ($wasRunning) { Start-Service -Name $serviceName }
  } catch {
    throw "Installation failed: $installationError. Rollback also failed: $_. Backup: $backupExe"
  }
  throw $installationError
} finally {
  if (Test-Path -LiteralPath $stagedExe) { Remove-Item -LiteralPath $stagedExe -Force }
}
if (Test-Path -LiteralPath $backupExe) { Remove-Item -LiteralPath $backupExe -Force }
Write-Output "Enhanced Windows experience installed. Reboot required by drivers: $rebootRequired"
