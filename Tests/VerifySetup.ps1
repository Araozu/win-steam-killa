$ErrorActionPreference = 'Stop'
$Repo = Split-Path $PSScriptRoot -Parent
foreach ($Name in @('install', 'uninstall')) {
    $Tokens = $null
    $ParseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        "$Repo/Setup/$Name.ps1", [ref]$Tokens, [ref]$ParseErrors) | Out-Null
    if ($ParseErrors.Count) { throw ($ParseErrors | Out-String) }
}

# Run only the publish-failure path with mocked external commands. A failed
# native executable must abort before any installed service or files change.
function Get-ScheduledTask { param($TaskName) return $null }
function dotnet {
    $script:PublishArguments = $args
    $global:LASTEXITCODE = 42
}
$Install = (Get-Content "$Repo/Setup/install.ps1" -Raw) -replace '(?m)^#Requires.*$', ''
$SetupPath = Join-Path $Repo 'Setup'
$Install = $Install.Replace('$PSScriptRoot', '$SetupPath')
$FailedAsExpected = $false
try { & ([scriptblock]::Create($Install)) }
catch {
    if ($_.Exception.Message -notlike '*dotnet failed with exit code 42*') { throw }
    $FailedAsExpected = $true
}
if (-not $FailedAsExpected) { throw 'Installer ignored a failed publish.' }
if ($PublishArguments.Count -ne 10 -or $PublishArguments[0] -ne 'publish' -or $PublishArguments[2] -ne '-c') {
    throw 'Publish arguments were not forwarded intact.'
}
Write-Host 'Setup scripts parse; publish failures abort installation with intact arguments.'
