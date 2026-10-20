#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    & dotnet build Lending.sln --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'API solution build failed.' }
}
finally { Pop-Location }
