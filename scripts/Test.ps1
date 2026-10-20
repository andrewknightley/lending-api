#Requires -Version 7.0
[CmdletBinding()]
param([switch] $RequireSql)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$previousRequirement = $env:LENDING_REQUIRE_SQL_TESTS
Push-Location $repoRoot
try {
    if ($RequireSql) { $env:LENDING_REQUIRE_SQL_TESTS = '1' }
    & dotnet test Lending.sln --configuration Release --logger trx --results-directory TestResults
    if ($LASTEXITCODE -ne 0) { throw 'API tests failed.' }
}
finally {
    $env:LENDING_REQUIRE_SQL_TESTS = $previousRequirement
    Pop-Location
}
