[CmdletBinding()]
param(
    [string]$SdkVersion,
    [string]$ManifestVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:PackageProjectPath = Join-Path $script:RepoRoot 'RightSpeak.Package\RightSpeak.Package.wapproj'
$script:ManifestPath = Join-Path $script:RepoRoot 'RightSpeak.Package\Package.appxmanifest'
$script:AppPackagesPath = Join-Path $script:RepoRoot 'RightSpeak.Package\AppPackages'
$script:PackageBinReleasePath = Join-Path $script:RepoRoot 'RightSpeak.Package\bin\x64\Release'
$script:PackageObjReleasePath = Join-Path $script:RepoRoot 'RightSpeak.Package\obj\x64\Release'
$script:Vs2026MsbuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'

function Write-Step([string]$Message)
{
    Write-Host "[MSSTORE] $Message"
}

function Get-PinnedSdkVersion
{
    $globalJsonPath = Join-Path $script:RepoRoot 'global.json'
    if (Test-Path -LiteralPath $globalJsonPath)
    {
        $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
        if (-not $globalJson.sdk -or [string]::IsNullOrWhiteSpace($globalJson.sdk.version))
        {
            throw "global.json exists but sdk.version is missing."
        }

        return [string]$globalJson.sdk.version
    }

    if (-not [string]::IsNullOrWhiteSpace($SdkVersion))
    {
        return $SdkVersion
    }

    throw "Pinned SDK version was not found. Provide global.json sdk.version or pass -SdkVersion explicitly."
}

function Get-SdkInstallRoot([string]$ResolvedSdkVersion)
{
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue))
    {
        throw "dotnet CLI is required to verify SDK resolution."
    }

    $sdkMatches = @()
    $sdkLines = @(dotnet --list-sdks)
    foreach ($line in $sdkLines)
    {
        $trimmed = $line.Trim()
        if ($trimmed -notmatch '^(\d+\.\d+\.\d+)\s+\[(.+)\]$')
        {
            continue
        }

        $version = $matches[1]
        $root = $matches[2]
        if ($version -eq $ResolvedSdkVersion)
        {
            $sdkMatches += [pscustomobject]@{
                Version = $version
                SdkRoot = Join-Path $root $version
                DotnetRoot = Split-Path -Parent $root
            }
        }
    }

    if ($sdkMatches.Count -eq 0)
    {
        throw "Pinned SDK '$ResolvedSdkVersion' is not installed. dotnet --list-sdks did not report it."
    }

    $distinctSdkRoots = @($sdkMatches | Select-Object -ExpandProperty SdkRoot | Sort-Object -Unique)
    if ($distinctSdkRoots.Count -ne 1)
    {
        throw "Pinned SDK '$ResolvedSdkVersion' is installed in multiple roots. Refusing to package without a single SDK root."
    }

    $selected = $sdkMatches[0]
    $sdkRoot = $selected.SdkRoot
    $msbuildSdkPath = Join-Path $sdkRoot 'Sdks\\Microsoft.NET.Sdk\\Sdk'
    if (-not (Test-Path -LiteralPath $msbuildSdkPath))
    {
        throw "Pinned SDK '$ResolvedSdkVersion' is present but invalid for MSBuild SDK resolution. Missing '$msbuildSdkPath'."
    }

    return @{ DotnetRoot = $selected.DotnetRoot; SdkRoot = $sdkRoot }
}

function Assert-SdkResolverConsistency([string]$ResolvedSdkVersion, [string]$ExpectedDotnetRoot, [string]$ExpectedSdkRoot)
{
    $existingVer = $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER
    if (-not [string]::IsNullOrWhiteSpace($existingVer) -and $existingVer -ne $ResolvedSdkVersion)
    {
        throw "SDK resolver mismatch: DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER='$existingVer' but expected '$ResolvedSdkVersion'."
    }

    $existingCliRoot = $env:DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR
    if (-not [string]::IsNullOrWhiteSpace($existingCliRoot) -and
        ([System.IO.Path]::GetFullPath($existingCliRoot) -ne [System.IO.Path]::GetFullPath($ExpectedDotnetRoot)))
    {
        throw "SDK resolver mismatch: DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR='$existingCliRoot' but expected '$ExpectedDotnetRoot'."
    }

    $existingSdksDir = $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR
    if (-not [string]::IsNullOrWhiteSpace($existingSdksDir) -and
        ([System.IO.Path]::GetFullPath($existingSdksDir) -ne [System.IO.Path]::GetFullPath((Join-Path $ExpectedSdkRoot 'Sdks'))))
    {
        throw "SDK resolver mismatch: DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR='$existingSdksDir' but expected '$(Join-Path $ExpectedSdkRoot 'Sdks')'."
    }
}

function Set-SdkResolverEnvironment([string]$ResolvedSdkVersion, [string]$DotnetRoot, [string]$SdkRoot)
{
    $env:DOTNET_ROOT = $DotnetRoot
    $env:DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR = $DotnetRoot
    $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER = $ResolvedSdkVersion
    $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR = Join-Path $SdkRoot 'Sdks'
}

function Get-PreviousUploadArtifact
{
    if (-not (Test-Path -LiteralPath $script:AppPackagesPath))
    {
        return $null
    }

    return Get-ChildItem -LiteralPath $script:AppPackagesPath -File |
        Where-Object { $_.Extension -in '.msixupload', '.appxupload' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Get-InnerPackageEntries([System.IO.FileInfo]$UploadArtifact)
{
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($UploadArtifact.FullName)
    try
    {
        return @($zip.Entries | Where-Object {
            $_.FullName -match '\.(msix|appx|msixbundle|appxbundle)$'
        } | Select-Object -ExpandProperty FullName)
    }
    finally
    {
        $zip.Dispose()
    }
}

function Get-PreviousPackageShape([System.IO.FileInfo]$UploadArtifact)
{
    if ($null -eq $UploadArtifact)
    {
        return 'single'
    }

    $entries = @(Get-InnerPackageEntries -UploadArtifact $UploadArtifact)
    if ($entries.Count -eq 0)
    {
        throw "Upload artifact '$($UploadArtifact.FullName)' has no package entries."
    }

    $hasBundle = @($entries | Where-Object { $_ -match '\.(msixbundle|appxbundle)$' }).Count -gt 0
    if ($hasBundle)
    {
        return 'bundle'
    }

    return 'single'
}

function Get-ManifestIdentityVersion
{
    [xml]$manifest = Get-Content -LiteralPath $script:ManifestPath -Raw
    $identity = $manifest.Package.Identity
    if ($null -eq $identity)
    {
        throw "Identity element not found in manifest '$script:ManifestPath'."
    }

    return [string]$identity.Version
}

function Set-ManifestIdentityVersion([string]$NewVersion)
{
    [xml]$manifest = Get-Content -LiteralPath $script:ManifestPath -Raw
    $identity = $manifest.Package.Identity
    if ($null -eq $identity)
    {
        throw "Identity element not found in manifest '$script:ManifestPath'."
    }

    $identity.Version = $NewVersion
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.NewLineChars = "`r`n"
    $settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace
    $settings.OmitXmlDeclaration = $false

    $writer = [System.Xml.XmlWriter]::Create($script:ManifestPath, $settings)
    try
    {
        $manifest.Save($writer)
    }
    finally
    {
        $writer.Dispose()
    }
}

function Get-IncrementedVersion([string]$CurrentVersion)
{
    $version = Parse-AndValidateStoreVersion -Version $CurrentVersion -Context 'Current manifest version'
    return "$($version.Major).$($version.Minor).$($version.Build + 1).0"
}

function Remove-StalePackageArtifacts
{
    $targets = @(
        $script:AppPackagesPath,
        (Join-Path $script:PackageBinReleasePath 'Upload'),
        (Join-Path $script:PackageObjReleasePath 'Upload'),
        (Join-Path $script:PackageObjReleasePath 'Upload.Symbols')
    )

    foreach ($target in $targets)
    {
        if (Test-Path -LiteralPath $target)
        {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }
}

function Invoke-StoreBuild([string]$PackageShape)
{
    if (-not (Test-Path -LiteralPath $script:Vs2026MsbuildPath))
    {
        throw "Visual Studio 2026 MSBuild not found at '$script:Vs2026MsbuildPath'."
    }

    $appxBundleValue = if ($PackageShape -eq 'bundle') { 'Always' } else { 'Never' }

    $arguments = @(
        $script:PackageProjectPath,
        '/restore',
        '/t:Build',
        '/m',
        '/p:Configuration=Release',
        '/p:Platform=x64',
        '/p:UapAppxPackageBuildMode=StoreUpload',
        '/p:AppxBundlePlatforms=x64',
        "/p:AppxBundle=$appxBundleValue"
    )

    & $script:Vs2026MsbuildPath @arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Store packaging build failed with exit code $LASTEXITCODE."
    }
}

function Get-NewUploadArtifact
{
    if (-not (Test-Path -LiteralPath $script:AppPackagesPath))
    {
        throw "AppPackages output path not found after build: '$script:AppPackagesPath'."
    }

    $artifact = Get-ChildItem -LiteralPath $script:AppPackagesPath -File |
        Where-Object { $_.Extension -in '.msixupload', '.appxupload' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $artifact)
    {
        throw "No .msixupload or .appxupload artifact was generated."
    }

    return $artifact
}

function Assert-UploadIsX64Only([System.IO.FileInfo]$UploadArtifact)
{
    $entries = @(Get-InnerPackageEntries -UploadArtifact $UploadArtifact)
    if ($entries.Count -eq 0)
    {
        throw "Upload artifact '$($UploadArtifact.FullName)' has no embedded package entries."
    }

    $nonX64 = @($entries | Where-Object { $_ -match '(_x86|_arm64|_arm|_neutral)(\.|_)' })
    if ($nonX64.Count -gt 0)
    {
        $joined = $nonX64 -join ', '
        throw "Upload artifact contains non-x64 package entries: $joined"
    }

    $hasX64 = @($entries | Where-Object { $_ -match '_x64(\.|_)' }).Count -gt 0
    if (-not $hasX64)
    {
        throw "Upload artifact does not contain any x64 package entry."
    }
}

function Assert-UploadVersion([System.IO.FileInfo]$UploadArtifact, [string]$ExpectedVersion)
{
    if ($UploadArtifact.Name -notmatch [regex]::Escape($ExpectedVersion))
    {
        throw "Generated upload artifact '$($UploadArtifact.Name)' does not include expected version '$ExpectedVersion'."
    }

    $entries = @(Get-InnerPackageEntries -UploadArtifact $UploadArtifact)
    $mismatched = @($entries | Where-Object { $_ -match '\d+\.\d+\.\d+\.\d+' -and $_ -notmatch [regex]::Escape($ExpectedVersion) })
    if ($mismatched.Count -gt 0)
    {
        $joined = $mismatched -join ', '
        throw "Upload artifact includes stale package versions: $joined"
    }
}

function Parse-AndValidateStoreVersion([string]$Version, [string]$Context)
{
    if ([string]::IsNullOrWhiteSpace($Version))
    {
        throw "$Context is empty."
    }

    if ($Version -notmatch '^(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)\.(?<revision>\d+)$')
    {
        throw "$Context '$Version' is invalid. Expected format: Major.Minor.Build.0"
    }

    $major = [int]$matches['major']
    $minor = [int]$matches['minor']
    $build = [int]$matches['build']
    $revision = [int]$matches['revision']

    if ($revision -ne 0)
    {
        throw "$Context '$Version' is invalid for Store submission. Revision must be 0."
    }

    return [pscustomobject]@{
        Major = $major
        Minor = $minor
        Build = $build
        Revision = $revision
    }
}

function Get-ZipEntryTextByName([string]$ZipPath, [string]$EntryName)
{
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try
    {
        $entry = $zip.GetEntry($EntryName)
        if ($null -eq $entry)
        {
            throw "Entry '$EntryName' not found in '$ZipPath'."
        }

        $stream = $entry.Open()
        try
        {
            $reader = New-Object System.IO.StreamReader($stream)
            try
            {
                return $reader.ReadToEnd()
            }
            finally
            {
                $reader.Dispose()
            }
        }
        finally
        {
            $stream.Dispose()
        }
    }
    finally
    {
        $zip.Dispose()
    }
}

function Get-PackageIdentityVersionFromPackage([string]$PackagePath)
{
    [xml]$manifestXml = Get-ZipEntryTextByName -ZipPath $PackagePath -EntryName 'AppxManifest.xml'
    if ($null -eq $manifestXml.Package -or $null -eq $manifestXml.Package.Identity)
    {
        throw "AppxManifest identity not found in package '$PackagePath'."
    }

    return [string]$manifestXml.Package.Identity.Version
}

function Get-BundlePackageIdentityVersions([string]$BundlePath)
{
    [xml]$bundleManifest = Get-ZipEntryTextByName -ZipPath $BundlePath -EntryName 'AppxMetadata/AppxBundleManifest.xml'
    $ns = New-Object System.Xml.XmlNamespaceManager($bundleManifest.NameTable)
    $ns.AddNamespace('b', 'http://schemas.microsoft.com/appx/2013/bundle')

    $packageNodes = $bundleManifest.SelectNodes('//b:Packages/b:Package', $ns)
    if ($null -eq $packageNodes -or $packageNodes.Count -eq 0)
    {
        throw "Bundle '$BundlePath' has no package entries in AppxBundleManifest.xml."
    }

    return @($packageNodes | ForEach-Object { [string]$_.Version })
}

function Expand-UploadPackagesToTemp([System.IO.FileInfo]$UploadArtifact)
{
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rightspeak-store-upload-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempRoot | Out-Null

    try
    {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($UploadArtifact.FullName)
        try
        {
            $packageEntries = @($zip.Entries | Where-Object { $_.FullName -match '\.(msix|appx|msixbundle|appxbundle)$' })
            if ($packageEntries.Count -eq 0)
            {
                throw "Upload artifact '$($UploadArtifact.FullName)' has no embedded package entries."
            }

            $copied = @()
            foreach ($entry in $packageEntries)
            {
                $leaf = [System.IO.Path]::GetFileName($entry.FullName)
                $outPath = Join-Path $tempRoot $leaf
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $outPath, $true)
                $copied += $outPath
            }

            return @{ TempRoot = $tempRoot; PackagePaths = $copied }
        }
        finally
        {
            $zip.Dispose()
        }
    }
    catch
    {
        if (Test-Path -LiteralPath $tempRoot)
        {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }

        throw
    }
}

function Assert-UploadPackageIdentityVersions([System.IO.FileInfo]$UploadArtifact, [string]$ExpectedVersion)
{
    $expanded = Expand-UploadPackagesToTemp -UploadArtifact $UploadArtifact
    try
    {
        $versions = @()
        foreach ($packagePath in $expanded.PackagePaths)
        {
            if ($packagePath -match '\.(msixbundle|appxbundle)$')
            {
                $versions += Get-BundlePackageIdentityVersions -BundlePath $packagePath
                continue
            }

            $versions += Get-PackageIdentityVersionFromPackage -PackagePath $packagePath
        }

        if ($versions.Count -eq 0)
        {
            throw "No package identity versions were found in upload artifact '$($UploadArtifact.FullName)'."
        }

        $invalidRevision = @($versions | Where-Object {
            $v = Parse-AndValidateStoreVersion -Version $_ -Context 'Artifact package identity version'
            $v.Revision -ne 0
        })
        if ($invalidRevision.Count -gt 0)
        {
            throw "Upload artifact contains non-zero revision package identities: $($invalidRevision -join ', ')"
        }

        $mismatched = @($versions | Where-Object { $_ -ne $ExpectedVersion })
        if ($mismatched.Count -gt 0)
        {
            throw "Upload artifact package identities do not match expected version '$ExpectedVersion': $($mismatched -join ', ')"
        }
    }
    finally
    {
        if (Test-Path -LiteralPath $expanded.TempRoot)
        {
            Remove-Item -LiteralPath $expanded.TempRoot -Recurse -Force
        }
    }
}

Write-Step 'Starting Microsoft Store package generation flow.'

if (-not (Test-Path -LiteralPath $script:PackageProjectPath))
{
    throw "Package project not found: '$script:PackageProjectPath'."
}

if (-not (Test-Path -LiteralPath $script:ManifestPath))
{
    throw "Package manifest not found: '$script:ManifestPath'."
}

$pinnedSdkVersion = Get-PinnedSdkVersion
Write-Step "Resolved SDK version: $pinnedSdkVersion"
$sdkInfo = Get-SdkInstallRoot -ResolvedSdkVersion $pinnedSdkVersion
Assert-SdkResolverConsistency -ResolvedSdkVersion $pinnedSdkVersion -ExpectedDotnetRoot $sdkInfo.DotnetRoot -ExpectedSdkRoot $sdkInfo.SdkRoot
Set-SdkResolverEnvironment -ResolvedSdkVersion $pinnedSdkVersion -DotnetRoot $sdkInfo.DotnetRoot -SdkRoot $sdkInfo.SdkRoot
Write-Step "SDK resolver configured to: $($sdkInfo.SdkRoot)"

$previousArtifact = Get-PreviousUploadArtifact
$previousShape = Get-PreviousPackageShape -UploadArtifact $previousArtifact
if ($null -ne $previousArtifact)
{
    Write-Step "Previous upload artifact: $($previousArtifact.Name) (shape: $previousShape)"
}
else
{
    Write-Step 'No previous upload artifact detected; defaulting to single-package output.'
}

$manifestVersionBefore = Get-ManifestIdentityVersion
$null = Parse-AndValidateStoreVersion -Version $manifestVersionBefore -Context 'Current manifest version'
$targetVersion = if (-not [string]::IsNullOrWhiteSpace($ManifestVersion)) { $ManifestVersion } else { Get-IncrementedVersion -CurrentVersion $manifestVersionBefore }
$null = Parse-AndValidateStoreVersion -Version $targetVersion -Context 'Target manifest version'

if ($targetVersion -eq $manifestVersionBefore)
{
    throw "Manifest version was not changed. Current version: '$manifestVersionBefore'."
}

Write-Step "Updating manifest version: $manifestVersionBefore -> $targetVersion"
Set-ManifestIdentityVersion -NewVersion $targetVersion

Write-Step 'Cleaning stale package artifacts from AppPackages and Upload output folders.'
Remove-StalePackageArtifacts

Write-Step "Running VS2026 MSBuild Store upload (x64-only, shape: $previousShape)."
Invoke-StoreBuild -PackageShape $previousShape

$newArtifact = Get-NewUploadArtifact
Write-Step "Generated upload artifact: $($newArtifact.FullName)"
Assert-UploadIsX64Only -UploadArtifact $newArtifact
Assert-UploadVersion -UploadArtifact $newArtifact -ExpectedVersion $targetVersion
Assert-UploadPackageIdentityVersions -UploadArtifact $newArtifact -ExpectedVersion $targetVersion

Write-Step 'Verification passed: x64-only upload artifact with updated manifest version.'
Write-Host "UPLOAD_ARTIFACT=$($newArtifact.FullName)"
Write-Host "MANIFEST_VERSION=$targetVersion"
