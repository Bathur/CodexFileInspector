[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$version = '15.2.0'
$archiveName = "ripgrep-$version-x86_64-pc-windows-msvc.zip"
$releaseBase = "https://github.com/BurntSushi/ripgrep/releases/download/$version"
$expectedArchiveSha256 = '71b2fef860abe467217a538ff31de02f5258807c0129f771846f87bd029aafc5'
$downloadsDirectory = Join-Path $repositoryRoot '.local\downloads\ripgrep'
$archivePath = Join-Path $downloadsDirectory $archiveName
$checksumPath = "$archivePath.sha256"
$toolDirectory = Join-Path $repositoryRoot ".local\tools\ripgrep\$version\win-x64"
$temporaryRoot = Join-Path $repositoryRoot '.local\temp'
$temporaryDirectory = Join-Path $temporaryRoot ([Guid]::NewGuid().ToString('N'))

function Assert-RepositoryLocalPath {
    param([Parameter(Mandatory)][string] $Path)

    $fullRepositoryRoot = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullRepositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the repository: $fullPath"
    }
}

foreach ($path in @($downloadsDirectory, $toolDirectory, $temporaryDirectory)) {
    Assert-RepositoryLocalPath $path
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}

try {
    Invoke-WebRequest -Uri "$releaseBase/$archiveName" -OutFile $archivePath
    Invoke-WebRequest -Uri "$releaseBase/$archiveName.sha256" -OutFile $checksumPath

    $checksumDocument = Get-Content -Raw -LiteralPath $checksumPath
    $publishedChecksumMatch = [Regex]::Match($checksumDocument, '(?im)^[0-9a-f]{64}\s*$')
    if (-not $publishedChecksumMatch.Success) {
        throw 'The official checksum document does not contain a standalone SHA-256 value.'
    }

    $publishedChecksum = $publishedChecksumMatch.Value.Trim().ToLowerInvariant()
    if ($publishedChecksum -ne $expectedArchiveSha256) {
        throw "Published checksum '$publishedChecksum' does not match the repository-pinned checksum."
    }

    $actualChecksum = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
    if ($actualChecksum -ne $expectedArchiveSha256) {
        throw "Downloaded ripgrep checksum '$actualChecksum' does not match '$expectedArchiveSha256'."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $temporaryDirectory
    $archiveRoot = Join-Path $temporaryDirectory "ripgrep-$version-x86_64-pc-windows-msvc"

    foreach ($fileName in @('rg.exe', 'LICENSE-MIT', 'UNLICENSE')) {
        $sourcePath = Join-Path $archiveRoot $fileName
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "The official archive is missing $fileName."
        }

        Copy-Item -Force -LiteralPath $sourcePath -Destination (Join-Path $toolDirectory $fileName)
    }

    $versionOutput = & (Join-Path $toolDirectory 'rg.exe') '--version'
    if ($LASTEXITCODE -ne 0 -or $versionOutput[0] -notmatch '^ripgrep 15\.2\.0') {
        throw "The acquired rg.exe did not report ripgrep 15.2.0."
    }

    Write-Host "Acquired $($versionOutput[0]) at $toolDirectory"
    Write-Host "Verified archive SHA-256: $actualChecksum"
}
finally {
    Assert-RepositoryLocalPath $temporaryDirectory
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -Recurse -Force -LiteralPath $temporaryDirectory
    }
}
