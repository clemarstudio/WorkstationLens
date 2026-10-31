param(
    [string]$Version = '0.1.0',
    [switch]$SkipBuild,
    [string]$TestSlot = 'customer-test'
)

# Intentionally does not launch Setup.exe, alter the Windows Scheduler,
# sign, publish a release, or touch the daily Collector database.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($TestSlot -notmatch '^customer-test(?:-[a-zA-Z0-9-]{1,40})?$') { throw 'Invalid isolated test slot.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releaseDir = Join-Path $env:LOCALAPPDATA "WorkstationLens\release-build\$TestSlot\releases"
$pack = Join-Path $PSScriptRoot 'Pack-Windows.ps1'

if (-not $SkipBuild) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $pack -Version $Version -UnsignedLocalTest -TestSlot $TestSlot
    if ($LASTEXITCODE -ne 0) { throw 'Unsigned customer package build failed.' }
}
$setup = Join-Path $releaseDir 'Clemar.WorkstationLens-win-Setup.exe'
if (-not (Test-Path -LiteralPath $setup)) { throw "Expected installer missing: $setup" }

# Follow the official Windows Sandbox .wsb mapped-folder contract.
# Map ONLY the generated releases directory, READ ONLY, with no networking
# or host-drive mapping. The user decides whether/when to launch Sandbox.
$hostFolder = [System.Security.SecurityElement]::Escape($releaseDir)
$wsb = @"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Disable</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$hostFolder</HostFolder>
      <SandboxFolder>C:\WorkstationLens-Customer-Test</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>explorer.exe C:\WorkstationLens-Customer-Test</Command>
  </LogonCommand>
</Configuration>
"@
$config = Join-Path $releaseDir 'Launch-Windows-Sandbox.wsb'
[System.IO.File]::WriteAllText($config, $wsb, [System.Text.UTF8Encoding]::new($false))

$smokeSource = Join-Path $PSScriptRoot 'Verify-Customer-Install.ps1'
Copy-Item -LiteralPath $smokeSource -Destination (Join-Path $releaseDir 'Verify-Customer-Install.ps1') -Force

$instructions = @"
WORKSTATION LENS - CUSTOMER INSTALL ACCEPTANCE (UNSIGNED LOCAL TEST)

HOST:
  $config

1. Double-click Launch-Windows-Sandbox.wsb from Explorer.
   If Windows Sandbox is not enabled, use Windows Optional Features (requires
   Administrator and potentially a restart) or an isolated Windows VM.
2. Inside Sandbox, a read-only C:\WorkstationLens-Customer-Test folder opens.
3. Double-click Clemar.WorkstationLens-win-Setup.exe to install.
   Do not run this unsigned test installer on your daily Windows account.
4. Inside Sandbox, open http://127.0.0.1:18765/ in its own browser, or
   launch the Workstation Lens Start Menu shortcut.
5. Inside Sandbox, open PowerShell and run:
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\WorkstationLens-Customer-Test\Verify-Customer-Install.ps1"
6. Confirm Live readings appear before first saved minute. Hover over
   charts, confirm there is no CMD window. Use Export to directly download
   one ZIP (CSV + Markdown + JSON) without a dialog. Select Custom for
   local From/To dates and test the matching ZIP. Check version
   under /api/update (installed build must say canAutoUpdate=true).
7. Close Windows Sandbox when done. It permanently discards the Sandbox
   app and database. Your HOST Collector and original SQLite remain intact.

Notes:
- This unsigned local validation checks first install and collector basics,
  NOT authentic publisher signing nor a real 1.0.0 -> 1.0.1 update.
- Sandbox networking is disabled deliberately; no GitHub update check
  can succeed in this specific test.
- Sandbox vGPU is disabled; missing physical GPU metrics there is expected.
- For upgrade validation use a persisted Windows VM, two signed versions,
  and a test release feed. See RELEASE_V1.md.
"@
[System.IO.File]::WriteAllText((Join-Path $releaseDir 'CUSTOMER_TEST_README.txt'),
    $instructions, [System.Text.UTF8Encoding]::new($false))

# Temporary self-contained dotnet publish files are no longer needed once
# Velopack made Setup.exe and the versioned release package. Keep only the
# test packages and scripts so that this workflow wastes less C: disk space.
$publishDir = Join-Path $env:LOCALAPPDATA "WorkstationLens\release-build\$TestSlot\publish"
$expectedPublish = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "WorkstationLens\release-build\$TestSlot\publish"))
if ([IO.Path]::GetFullPath($publishDir) -ne $expectedPublish) {
    throw 'Refusing cleanup outside the disposable customer-test publish directory.'
}
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

Write-Host 'Local test files ready; NO installer was launched:' -ForegroundColor Green
Get-Item $setup,$config,(Join-Path $releaseDir 'Verify-Customer-Install.ps1') |
    Select-Object Name,FullName,@{Name='MiB';Expression={[Math]::Round($_.Length/1MB,2)}} |
    Format-Table -AutoSize
