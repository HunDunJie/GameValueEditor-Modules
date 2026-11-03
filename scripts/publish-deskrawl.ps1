[CmdletBinding()]
param(
    [ValidatePattern('^$|^(0|[1-9][0-9]*)\.[0-9]\.[0-9]$')]
    [string]$Version = '',
    [string]$OutputDirectory = '',
    [switch]$CatalogOnly,
    [switch]$VerifyReleasedVersion
)

$ErrorActionPreference = 'Stop'
# All path bounds, clean-source provenance and immutable archive checks stay shared.
& (Join-Path $PSScriptRoot 'Publish-GameModule.ps1') -Game deskrawl -Version $Version `
    -OutputDirectory $OutputDirectory -SkipCatalog:(-not $CatalogOnly) `
    -CatalogOnly:$CatalogOnly -VerifyReleasedVersion:$VerifyReleasedVersion
