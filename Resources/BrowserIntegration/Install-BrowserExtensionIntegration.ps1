param(
    [Parameter(Mandatory = $true)]
    [string]$ExtensionId,
    [string[]]$AdditionalExtensionIds = @(),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$Framework = "net10.0-windows",
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [switch]$RunBridgeTest
)

$ErrorActionPreference = "Stop"

function Normalize-ExtensionId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $normalized = $Value.Trim().ToLowerInvariant()
    if ($normalized -notmatch "^[a-p]{32}$") {
        throw "Invalid extension ID: '$Value'. Expected 32 lowercase characters in the range a-p."
    }

    return $normalized
}

$hostExecutablePath = Join-Path $ProjectRoot "RightSpeak.NativeHost\bin\$Configuration\$Framework\RightSpeak.NativeHost.exe"
$installScriptPath = Join-Path $PSScriptRoot "Install-NativeHost.ps1"
$testScriptPath = Join-Path $PSScriptRoot "Test-BrowserIntegration.ps1"
$manifestPath = Join-Path $env:LOCALAPPDATA "RightSpeak\NativeHost\com.rightspeak.bridge.json"

if (-not (Test-Path $hostExecutablePath)) {
    throw "RightSpeak native host executable not found: $hostExecutablePath. Build the solution first."
}

$normalizedIds = @()
$normalizedIds += Normalize-ExtensionId -Value $ExtensionId
if ($AdditionalExtensionIds) {
    $normalizedIds += $AdditionalExtensionIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Normalize-ExtensionId -Value $_ }
}

$normalizedIds = $normalizedIds | Select-Object -Unique
$allOrigins = $normalizedIds | ForEach-Object { "chrome-extension://$_/" }

if (Test-Path $manifestPath) {
    try {
        $existingManifest = Get-Content -Raw -Path $manifestPath | ConvertFrom-Json
        if ($existingManifest.allowed_origins) {
            $allOrigins += @($existingManifest.allowed_origins)
        }
    }
    catch {
        Write-Warning "Existing native host manifest could not be parsed. It will be replaced."
    }
}

$allOrigins = $allOrigins |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -Unique

& $installScriptPath -HostExecutablePath $hostExecutablePath -ExtensionOrigins $allOrigins

Write-Host ""
Write-Host "Browser integration setup complete."
Write-Host "Host executable: $hostExecutablePath"
Write-Host "Configured extension IDs:"
$normalizedIds | ForEach-Object { Write-Host "- $_" }
Write-Host ""
Write-Host "Next steps:"
Write-Host "1. Ensure RightSpeak is running."
Write-Host "2. Reload extension in chrome://extensions or edge://extensions."
Write-Host "3. Select text in browser, then use context menu: Read with RightSpeak."

if ($RunBridgeTest) {
    if (-not (Test-Path $testScriptPath)) {
        throw "Bridge test script not found: $testScriptPath"
    }

    Write-Host ""
    Write-Host "Running bridge verification..."
    & $testScriptPath -Configuration $Configuration -Framework $Framework -ProjectRoot $ProjectRoot
}
