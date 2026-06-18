<#
  build-package.ps1 — builds an installable PlayIt Live plugin package for Partyline
  and (optionally) installs it by calling PlayIt.Plugins.Installer.exe.

  The package is a PLAIN ZIP containing:
     manifest.json          (Label "Partyline" -> installs to ...\PlayIt Live\Plugins\Partyline)
     Partyline.pips         (the plaintext bootstrapper PlayIt compiles)
     PartylinePlugin.dll    (Costura-merged plugin assembly)
  PlayIt Live reads it directly as a zip; no encryption is required for your own plugin.

  Usage (run on Windows, from the plugin repo root or anywhere):
     powershell -ExecutionPolicy Bypass -File packaging\build-package.ps1
     powershell ... build-package.ps1 -Install          # also invoke the installer
#>
param(
  [string]$Configuration = "Release",
  [string]$PipsName      = "Partyline.pips",            # name of your bootstrapper .pips
  [string]$InstallerExe  = "PlayIt.Plugins.Installer.exe",
  [switch]$Install
)
$ErrorActionPreference = "Stop"

$packagingDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginRoot   = Split-Path -Parent $packagingDir
$dll      = Join-Path $pluginRoot "bin\$Configuration\net48\PartylinePlugin.dll"
$pips     = Join-Path $pluginRoot $PipsName
$manifest = Join-Path $packagingDir "manifest.json"

foreach ($f in @($dll, $pips, $manifest)) {
  if (-not (Test-Path $f)) { throw "Required file not found: $f" }
}

# Stage the package contents in a clean temp folder.
$stage = Join-Path $env:TEMP ("partyline-pkg-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item $manifest (Join-Path $stage "manifest.json")
Copy-Item $pips     (Join-Path $stage $PipsName)
Copy-Item $dll      (Join-Path $stage "PartylinePlugin.dll")

# Build the zip, then give it the .pips package extension PlayIt associates with the installer.
$outDir = Join-Path $pluginRoot "dist"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$zipPath = Join-Path $outDir "Partyline.zip"
$pkgPath = Join-Path $outDir "Partyline.pips"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
if (Test-Path $pkgPath) { Remove-Item $pkgPath -Force }

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force
Move-Item $zipPath $pkgPath -Force
Remove-Item $stage -Recurse -Force

Write-Host "Built package: $pkgPath"

if ($Install) {
  $installer = Get-Command $InstallerExe -ErrorAction SilentlyContinue
  if (-not $installer) { throw "Installer not found on PATH: $InstallerExe (pass -InstallerExe with a full path)" }
  Write-Host "Installing via $($installer.Source) ..."
  & $installer.Source $pkgPath
}
