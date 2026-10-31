param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$SignThumbprint,
    [switch]$UnsignedLocalTest,
    [string]$TestSlot = 'customer-test'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use three-part semver e.g. 1.0.0' }
if (-not $UnsignedLocalTest -and [string]::IsNullOrWhiteSpace($SignThumbprint)) {
    throw 'Release builds require code signing. Specify -SignThumbprint or -UnsignedLocalTest (NEVER publish unsigned).'
}
if ($UnsignedLocalTest -and -not [string]::IsNullOrWhiteSpace($SignThumbprint)) { throw 'Choose signed release OR unsigned local test.' }
if ($TestSlot -notmatch '^customer-test(?:-[a-zA-Z0-9-]{1,40})?$') { throw 'Invalid isolated test slot.' }
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Keep the large self-contained runtime and installers OUTSIDE OneDrive.
# Source remains in the repository; only the resulting artifacts live here.
$buildSubfolder = if ($UnsignedLocalTest) { "WorkstationLens\release-build\$TestSlot" }
else { 'WorkstationLens\release-build\signed-release' }
$buildBase=Join-Path $env:LOCALAPPDATA $buildSubfolder
$publish=Join-Path $buildBase 'publish'
$output=Join-Path $buildBase 'releases'
$project=Join-Path $repo 'WorkstationLens.App\WorkstationLens.App.csproj'
Push-Location $repo
try {
    if (-not $UnsignedLocalTest) {
        if (git status --porcelain) { throw 'Commit changes before a signed release.' }
        $thumb=$SignThumbprint -replace '[^0-9A-Fa-f]',''
        if ($thumb.Length -ne 40 -and $thumb.Length -ne 64) { throw 'Invalid certificate thumbprint.' }
        $cert=Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumb" -ErrorAction SilentlyContinue
        if (-not $cert -or -not $cert.HasPrivateKey) { throw 'Signing certificate/private key unavailable; check EV token.' }
    }
    $tool=dotnet tool list --local | Out-String
    if ($LASTEXITCODE -ne 0 -or $tool -notmatch '(?m)^vpk\s') { throw 'Run dotnet tool restore first.' }
    # Velopack refuses to create the *same version* in an existing feed.
    # For disposable unsigned tests only, reset the precisely named test
    # output directory; NEVER touch the signed release feed or measurements.db.
    if ($UnsignedLocalTest -and (Test-Path -LiteralPath $output)) {
        $expected=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "WorkstationLens\release-build\$TestSlot\releases"))
        if ([IO.Path]::GetFullPath($output) -ne $expected) {
            throw 'Refusing to clear unexpected customer test folder.'
        }
        try {
            Remove-Item -LiteralPath $output -Force -Recurse -ErrorAction Stop
        } catch [System.IO.IOException] {
            throw "The test folder is locked (e.g. open Windows Sandbox). Choose a fresh -TestSlot customer-test-v2 for another isolated output folder."
        }
    }
    New-Item -ItemType Directory -Path $publish,$output -Force | Out-Null
    & dotnet publish $project -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $publish --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    $arguments=@('tool','run','vpk','--','pack','--packId','Clemar.WorkstationLens','--packVersion',$Version,'--packTitle','Workstation Lens','--packDir',$publish,'--mainExe','WorkstationLens.App.exe','--runtime','win-x64','--outputDir',$output)
    if (-not $UnsignedLocalTest) {
        $arguments+=@('--signParams',"/sha1 $thumb /fd sha256 /td sha256 /tr http://timestamp.digicert.com",'--signParallel','1')
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
    $setups=@(Get-ChildItem -LiteralPath $output -Filter '*Setup.exe')
    if ($setups.Count -lt 1) { throw 'No Setup.exe produced.' }
    if (-not $UnsignedLocalTest) {
        foreach ($setup in $setups) {
            $sig=Get-AuthenticodeSignature -LiteralPath $setup.FullName
            if ($sig.Status -ne 'Valid') { throw "Installer signature check failed: $($setup.Name), $($sig.Status)" }
        }
    }
    Get-ChildItem -LiteralPath $output -File | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(),$_.Name
    } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding Ascii
    Write-Host "Packaged version $Version in $output (NOT uploaded)." -ForegroundColor Green
    if ($UnsignedLocalTest) { Write-Warning 'Unsigned TEST ONLY. Do not upload/distribute.' }
} finally { Pop-Location }
