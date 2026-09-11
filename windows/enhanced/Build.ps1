[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\Tests\PowerShellSyntax.ps1"
& "$PSScriptRoot\Tests\Installer.ps1"
& $Dotnet run --project "$PSScriptRoot\Tests\Tests.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Protocol tests failed' }
& $Dotnet publish "$PSScriptRoot\Guest\Guest.csproj" -m:1 -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o "$PSScriptRoot\artifacts\win-x64"
if ($LASTEXITCODE -ne 0) { throw 'Windows package build failed' }
& $Dotnet publish "$PSScriptRoot\WindowsTests\WindowsTests.csproj" -m:1 -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o "$PSScriptRoot\artifacts\windows-tests"
if ($LASTEXITCODE -ne 0) { throw 'Windows test package build failed' }
Copy-Item "$PSScriptRoot\Install.ps1", "$PSScriptRoot\Uninstall.ps1" -Destination "$PSScriptRoot\artifacts"
Copy-Item "$PSScriptRoot\README.md", "$PSScriptRoot\VERIFICATION.md" -Destination "$PSScriptRoot\artifacts"
Copy-Item "$PSScriptRoot\..\..\LICENSE" -Destination "$PSScriptRoot\artifacts"
$archive = "$PSScriptRoot\artifacts\EnhancedWindows-win-x64.zip"
Compress-Archive -Path "$PSScriptRoot\artifacts\win-x64", "$PSScriptRoot\artifacts\Install.ps1", `
  "$PSScriptRoot\artifacts\Uninstall.ps1", "$PSScriptRoot\artifacts\README.md", "$PSScriptRoot\artifacts\LICENSE", `
  "$PSScriptRoot\artifacts\windows-tests", "$PSScriptRoot\artifacts\VERIFICATION.md" `
  -DestinationPath $archive -Force
(Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash | Set-Content "$archive.sha256"
