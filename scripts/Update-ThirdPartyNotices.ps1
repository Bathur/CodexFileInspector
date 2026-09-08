[CmdletBinding()]
param([switch] $Write)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packageRoot = Join-Path $repositoryRoot '.local/nuget/packages'
$noticeRoot = Join-Path $repositoryRoot 'third_party/dotnet'
$lockFile = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'src/CodexFileInspector/packages.lock.json') | ConvertFrom-Json -AsHashtable
$targetFramework = @($lockFile.dependencies.Keys)[0]
$records = @()
$noticeCopies = @{}

foreach ($id in @($lockFile.dependencies[$targetFramework].Keys | Sort-Object)) {
    $locked = $lockFile.dependencies[$targetFramework][$id]
    $version = $locked.resolved
    if ($id -notmatch '^[A-Za-z0-9_.-]+$' -or $version -notmatch '^[A-Za-z0-9.+-]+$') {
        throw 'Unexpected package identity in the lock file.'
    }
    $directory = Join-Path $packageRoot ($id.ToLowerInvariant() + '/' + $version.ToLowerInvariant())
    $metadataPath = Join-Path $directory ($id.ToLowerInvariant() + '.nuspec')
    [xml] $metadataDocument = Get-Content -Raw -LiteralPath $metadataPath
    $metadata = $metadataDocument.package.metadata
    if ($metadata.id -cne $id -or $metadata.version -cne $version) { throw "Package identity mismatch: $id" }
    $cachedMetadata = Get-Content -Raw -LiteralPath (Join-Path $directory '.nupkg.metadata') | ConvertFrom-Json -AsHashtable
    if ($cachedMetadata.contentHash -cne $locked.contentHash) { throw "Restored package content hash differs from the lock file: $id" }
    # Signed .nupkg archive hashes can differ from NuGet's normalized lock-file content hash.
    $archivePath = Join-Path $directory ($id.ToLowerInvariant() + '.' + $version.ToLowerInvariant() + '.nupkg')
    $cachedArchiveHash = (Get-Content -Raw -LiteralPath "$archivePath.sha512").Trim()
    $actualArchiveHash = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash))
    if ($actualArchiveHash -cne $cachedArchiveHash) { throw "Cached package archive hash mismatch: $id" }
    $expression = [string] $metadata.license.'#text'
    if ($metadata.license.type -ne 'expression' -or $expression -notin @('MIT', 'Apache-2.0')) {
        throw "Review the license before updating this inventory: $id"
    }
    $sourceRepository = [string] $metadata.repository.url
    $sourceCommit = [string] $metadata.repository.commit
    if ($sourceRepository -notmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
        throw "Missing pinned upstream source: $id"
    }
    $licenseFile = switch ($sourceRepository) {
        'https://github.com/dotnet/dotnet' { 'MIT-dotnet.txt' }
        'https://github.com/dotnet/extensions' { 'MIT-extensions.txt' }
        'https://github.com/modelcontextprotocol/csharp-sdk' { 'MCP-SDK-LICENSE.txt' }
        default { throw "Review the upstream license text for $id before extending this script." }
    }
    $packageNotices = @()
    foreach ($notice in @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match '(?i)(notice|copyright)' -and $_.Extension -in @('.txt', '.md', '') } | Sort-Object Name)) {
        $digest = (Get-FileHash -LiteralPath $notice.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $relativeNotice = "notices/$digest.txt"
        $noticeCopies[$relativeNotice] = $notice.FullName
        $packageNotices += [ordered]@{ original_name = $notice.Name; file = $relativeNotice; sha256 = $digest }
    }
    $records += [ordered]@{
        id = $id
        version = $version
        license = $expression
        license_file = $licenseFile
        copyright = [string] $metadata.copyright
        source_repository = $sourceRepository
        source_commit = $sourceCommit
        source_archive = "$sourceRepository/archive/$sourceCommit.tar.gz"
        nuget_content_hash = $locked.contentHash
        notices = $packageNotices
    }
}

$inventory = [ordered]@{ schema_version = 1; target_framework = $targetFramework; packages = $records }
if ($Write) {
    New-Item -ItemType Directory -Path (Join-Path $noticeRoot 'notices') -Force | Out-Null
    foreach ($relativeNotice in $noticeCopies.Keys) {
        Copy-Item -LiteralPath $noticeCopies[$relativeNotice] -Destination (Join-Path $noticeRoot $relativeNotice) -Force
    }
    $inventoryJson = ($inventory | ConvertTo-Json -Depth 10).Replace("`r`n", "`n") + "`n"
    [IO.File]::WriteAllText((Join-Path $noticeRoot 'packages.json'), $inventoryJson, [Text.UTF8Encoding]::new($false))
}

[pscustomobject]@{
    Packages = $records.Count
    LicenseExpressions = @($records.license | Sort-Object -Unique)
    UniqueNoticeFiles = $noticeCopies.Count
    SourceRepositories = @($records.source_repository | Sort-Object -Unique)
    Written = $Write.IsPresent
} | ConvertTo-Json -Depth 4
