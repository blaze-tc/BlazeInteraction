[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('UnsafeDeletion', 'FrameworkDependent', 'PrepareOutput', 'ValidateOutput')]
    [string]$Scenario,
    [Parameter(Mandatory)]
    [string]$FixtureRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repositoryRoot 'scripts\bridge-release-functions.ps1')

$payload = Join-Path $FixtureRoot 'payload'
$profiles = Join-Path $FixtureRoot 'source-profiles'
switch ($Scenario) {
    'UnsafeDeletion' {
        $actual = Join-Path $FixtureRoot 'unsafe-target'
        $expected = Join-Path $FixtureRoot 'expected-target'
        Invoke-BridgePublishPreflight -OutputDirectory $actual -ExpectedOutputDirectory $expected `
            -EmbeddedDirectory $actual -ExpectedEmbeddedDirectory $actual `
            -FrameworkDependent $false -SkipUnityPackageEmbedding $false
    }
    'FrameworkDependent' {
        $target = Join-Path $FixtureRoot 'framework-dependent'
        Invoke-BridgePublishPreflight -OutputDirectory $target -ExpectedOutputDirectory $target `
            -EmbeddedDirectory $target -ExpectedEmbeddedDirectory $target `
            -FrameworkDependent $true -SkipUnityPackageEmbedding $false
    }
    'PrepareOutput' {
        Prepare-BridgePublishOutput -Directory $payload `
            -DefaultProfile (Join-Path $profiles 'default-profile.json') `
            -F20Profile (Join-Path $profiles 'f20-profile.json') -Version '1.2.0'
    }
    'ValidateOutput' {
        Assert-InteractionBridgePayload -Directory $FixtureRoot -ExpectedVersion '1.0.0'
    }
}
