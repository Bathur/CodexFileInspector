[CmdletBinding()]
param(
    [ValidateSet('Restore', 'Build', 'Test', 'Publish', 'All')]
    [string] $Target = 'All',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'This build and bundled ripgrep distribution target Windows x64. Use PowerShell 7 on a supported Windows x64 host.'
}
$repositoryRoot = $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'CodexFileInspector.slnx'
$serverProjectPath = Join-Path $repositoryRoot 'src\CodexFileInspector\CodexFileInspector.csproj'
$testProjectPath = Join-Path $repositoryRoot 'tests\CodexFileInspector.Tests\CodexFileInspector.Tests.csproj'
$serverProjectText = Get-Content -Raw -LiteralPath $serverProjectPath
$serverVersionMatch = [Regex]::Match($serverProjectText, '<Version>([^<]+)</Version>')
if (-not $serverVersionMatch.Success) {
    throw 'Could not determine the Server version from CodexFileInspector.csproj.'
}
$expectedServerVersion = $serverVersionMatch.Groups[1].Value
$publishDirectory = Join-Path $repositoryRoot ".artifacts\publish\win-x64\$expectedServerVersion"

$env:DOTNET_CLI_HOME = Join-Path $repositoryRoot '.local\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repositoryRoot '.local\nuget\http-cache'
$env:NUGET_PACKAGES = Join-Path $repositoryRoot '.local\nuget\packages'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $repositoryRoot '.local\nuget\plugins-cache'
$env:NUGET_XMLDOC_MODE = 'skip'
$env:TEMP = Join-Path $repositoryRoot '.local\temp'
$env:TMP = $env:TEMP

$localDirectories = @(
    $env:DOTNET_CLI_HOME,
    $env:NUGET_HTTP_CACHE_PATH,
    $env:NUGET_PACKAGES,
    $env:NUGET_PLUGINS_CACHE_PATH,
    $env:TEMP,
    (Join-Path $repositoryRoot '.artifacts')
)

foreach ($directory in $localDirectories) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Assert-RepositoryArtifactPath {
    param([Parameter(Mandatory)][string] $Path)

    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.artifacts')).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a generated-artifact path outside $artifactRoot"
    }
    $ancestor = $fullPath
    while ($ancestor.Length -ge $artifactRoot.TrimEnd('\').Length) {
        if (Test-Path -LiteralPath $ancestor) {
            if (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to modify an artifact path through a link: $ancestor"
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
}

function Test-DistributionLicenses {
    $sourceNoticeRoot = Join-Path $repositoryRoot 'third_party\dotnet'
    $publishedNoticeRoot = Join-Path $publishDirectory 'licenses\dotnet'
    $inventory = Get-Content -Raw -LiteralPath (Join-Path $sourceNoticeRoot 'packages.json') | ConvertFrom-Json -AsHashtable
    $locked = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'src\CodexFileInspector\packages.lock.json') | ConvertFrom-Json -AsHashtable
    $lockedPackages = $locked.dependencies[$inventory.target_framework]
    if ($inventory.packages.Count -ne $lockedPackages.Count) { throw 'Runtime license inventory is stale.' }
    $licensedPackageNames = @()
    foreach ($package in $inventory.packages) {
        if (-not $lockedPackages.ContainsKey($package.id) -or $lockedPackages[$package.id].resolved -cne $package.version -or $lockedPackages[$package.id].contentHash -cne $package.nuget_content_hash) {
            throw "License inventory does not match the locked package: $($package.id)"
        }
        $licensedPackageNames += "$($package.id)/$($package.version)"
        $sourceLicense = Join-Path $sourceNoticeRoot $package.license_file
        $publishedLicense = Join-Path $publishedNoticeRoot $package.license_file
        if ((Get-FileHash -LiteralPath $sourceLicense -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $publishedLicense -Algorithm SHA256).Hash) {
            throw "Published dependency license differs: $($package.id)"
        }
        foreach ($notice in $package.notices) {
            if ((Get-FileHash -LiteralPath (Join-Path $publishedNoticeRoot $notice.file) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $notice.sha256) {
                throw "Published dependency notice differs: $($package.id)"
            }
        }
    }
    $publishedDependencies = Get-Content -Raw -LiteralPath (Join-Path $publishDirectory 'CodexFileInspector.deps.json') | ConvertFrom-Json -AsHashtable
    $publishedPackages = @($publishedDependencies.libraries.Keys | Where-Object { $publishedDependencies.libraries[$_].type -eq 'package' } | Sort-Object)
    if (($publishedPackages -join '|') -cne (($licensedPackageNames | Sort-Object) -join '|')) {
        throw 'Published runtime dependencies differ from the license inventory.'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath (Join-Path $publishDirectory 'LICENSE') -Algorithm SHA256).Hash) {
        throw 'Published project license differs from the source license.'
    }
    Write-Host "Verified license inventory for $($inventory.packages.Count) runtime packages."
}

function Test-PublishedServer {
    Assert-RepositoryArtifactPath $publishDirectory
    $serverExecutable = Join-Path $publishDirectory 'CodexFileInspector.exe'
    $ripgrepExecutable = Join-Path $publishDirectory 'tools\ripgrep\rg.exe'
    $requiredFiles = @(
        $serverExecutable,
        (Join-Path $publishDirectory 'LICENSE'),
        (Join-Path $publishDirectory 'README.md'),
        (Join-Path $publishDirectory 'SOURCE.md'),
        (Join-Path $publishDirectory 'licenses\dotnet\packages.json'),
        (Join-Path $publishDirectory 'CodexFileInspector.dll'),
        (Join-Path $publishDirectory 'CodexFileInspector.deps.json'),
        (Join-Path $publishDirectory 'CodexFileInspector.runtimeconfig.json'),
        $ripgrepExecutable,
        (Join-Path $publishDirectory 'licenses\ripgrep\LICENSE-MIT'),
        (Join-Path $publishDirectory 'licenses\ripgrep\UNLICENSE'),
        (Join-Path $publishDirectory 'THIRD-PARTY-NOTICES.md')
    )

    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Published output is missing required file: $requiredFile"
        }
    }

    Test-DistributionLicenses
    $serverVersion = & $serverExecutable '--version'
    $expectedVersionPattern = [Regex]::Escape($expectedServerVersion)
    if ($LASTEXITCODE -ne 0 -or $serverVersion -notmatch "^Codex File Inspector $expectedVersionPattern(?:\.0)?$") {
        throw "Published Server version probe failed: $serverVersion"
    }

    $ripgrepVersion = & $ripgrepExecutable '--version'
    if ($LASTEXITCODE -ne 0 -or $ripgrepVersion[0] -notmatch '^ripgrep 15\.2\.0') {
        throw "Published ripgrep version probe failed: $($ripgrepVersion[0])"
    }

    $previousServerPath = $env:CODEX_FILE_INSPECTOR_SERVER_PATH
    try {
        $env:CODEX_FILE_INSPECTOR_SERVER_PATH = $serverExecutable
        Invoke-DotNet @(
            'test', $testProjectPath,
            '--configuration', 'Release',
            '--no-restore',
            '--filter', 'FullyQualifiedName~StdioServerTests',
            '--results-directory', (Join-Path $repositoryRoot '.artifacts\test-results\published')
        )
    }
    finally {
        if ($null -eq $previousServerPath) {
            Remove-Item Env:CODEX_FILE_INSPECTOR_SERVER_PATH -ErrorAction SilentlyContinue
        }
        else {
            $env:CODEX_FILE_INSPECTOR_SERVER_PATH = $previousServerPath
        }
    }

    $manifestPath = Join-Path $publishDirectory 'SHA256SUMS'
    $manifestLines = Get-ChildItem -Recurse -File -LiteralPath $publishDirectory |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = [IO.Path]::GetRelativePath($publishDirectory, $_.FullName).Replace('\', '/')
            $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
            "$hash  $relativePath"
        }
    [IO.File]::WriteAllLines($manifestPath, $manifestLines, [Text.UTF8Encoding]::new($false))

    Write-Host "Published Server: $serverVersion"
    Write-Host "Published ripgrep: $($ripgrepVersion[0])"
    Write-Host "Publish directory: $publishDirectory"
}

Push-Location $repositoryRoot
try {
    if ($Target -in @('Restore', 'All')) {
        Invoke-DotNet @(
            'restore', $solutionPath,
            '--configfile', (Join-Path $repositoryRoot 'NuGet.Config'),
            '--packages', $env:NUGET_PACKAGES
        )
    }

    if ($Target -in @('Build', 'All')) {
        Invoke-DotNet @(
            'build', $solutionPath,
            '--configuration', $Configuration,
            '--no-restore'
        )
    }

    if ($Target -in @('Test', 'All')) {
        Invoke-DotNet @(
            'test', $solutionPath,
            '--configuration', $Configuration,
            '--no-build',
            '--results-directory', (Join-Path $repositoryRoot '.artifacts\test-results')
        )
    }

    if ($Target -eq 'Publish') {
        Assert-RepositoryArtifactPath $publishDirectory
        if (Test-Path -LiteralPath $publishDirectory) {
            Remove-Item -Recurse -Force -LiteralPath $publishDirectory
        }

        Invoke-DotNet @(
            'publish', $serverProjectPath,
            '--configuration', 'Release',
            '--no-restore',
            '--output', $publishDirectory,
            '--property:SelfContained=false',
            '--property:UseAppHost=true'
        )
        Test-PublishedServer
    }
}
finally {
    Pop-Location
}
