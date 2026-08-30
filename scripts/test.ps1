[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [switch]$Unity,
    [string]$UnityEditor,
    [switch]$IncludeSamples
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$arguments = @('test', 'RadarControl.sln', '-c', $Configuration, '--logger', 'console;verbosity=minimal')
if ($NoBuild) { $arguments += '--no-build' }

Push-Location $repositoryRoot
try {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }

    if ($Unity) {
        $unityArguments = @('-TestPlatform', 'All')
        if (-not [string]::IsNullOrWhiteSpace($UnityEditor)) {
            $unityArguments += @('-UnityEditor', $UnityEditor)
        }
        if ($IncludeSamples) {
            $unityArguments += '-IncludeSamples'
        }

        & (Join-Path $PSScriptRoot 'test-unity-package.ps1') @unityArguments
        if ($LASTEXITCODE -ne 0) { throw "Unity package tests failed with exit code $LASTEXITCODE." }
    }
}
finally {
    Pop-Location
}
