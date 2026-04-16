param(
    [Parameter(Mandatory = $true)]
    [string]$HostExecutablePath,
    [string]$ExtensionOrigin,
    [string[]]$ExtensionOrigins,
    [string]$HostName = "com.rightspeak.bridge"
)

$ErrorActionPreference = "Stop"

function Normalize-ExtensionOrigin {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Origin
    )

    $value = $Origin.Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    if ($value -match "^[a-p]{32}$") {
        return "chrome-extension://$value/"
    }

    if ($value.StartsWith("chrome-extension://", [System.StringComparison]::OrdinalIgnoreCase)) {
        if (-not $value.EndsWith("/")) {
            $value = "$value/"
        }

        if ($value -match "^chrome-extension://[a-p]{32}/$") {
            return $value.ToLowerInvariant()
        }
    }

    throw "Invalid extension origin or ID: '$Origin'. Expected 32-char extension ID or chrome-extension://<id>/ origin."
}

$manifestDir = Join-Path $env:LOCALAPPDATA "RightSpeak\NativeHost"
$manifestPath = Join-Path $manifestDir "$HostName.json"
$manifestTempPath = "$manifestPath.tmp"

if (-not (Test-Path $HostExecutablePath)) {
    throw "Host executable not found: $HostExecutablePath"
}

$resolvedHostPath = (Resolve-Path -Path $HostExecutablePath).Path

$allOrigins = @()
if ($ExtensionOrigins) {
    $allOrigins += $ExtensionOrigins
}
if ($ExtensionOrigin) {
    $allOrigins += $ExtensionOrigin
}

$allOrigins = $allOrigins |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object { Normalize-ExtensionOrigin $_ } |
    Select-Object -Unique

if (-not $allOrigins -or $allOrigins.Count -eq 0) {
    throw "At least one extension origin must be provided."
}

if (-not (Test-Path $manifestDir)) {
    New-Item -ItemType Directory -Path $manifestDir | Out-Null
}

$manifest = [ordered]@{
    name = $HostName
    description = "RightSpeak native messaging bridge"
    path = $resolvedHostPath
    type = "stdio"
    allowed_origins = @($allOrigins)
}

$manifestContent = $manifest | ConvertTo-Json -Depth 4

$writeSucceeded = $false
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    try {
        Set-Content -Path $manifestTempPath -Value $manifestContent -Encoding UTF8
        Move-Item -Path $manifestTempPath -Destination $manifestPath -Force
        $writeSucceeded = $true
        break
    }
    catch {
        Start-Sleep -Milliseconds 200
    }
}

if (-not $writeSucceeded) {
    throw "Failed to update native host manifest at $manifestPath because it is locked by another process."
}

$chromeKey = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName"
$edgeKey = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$HostName"

New-Item -Path $chromeKey -Force | Out-Null
Set-ItemProperty -Path $chromeKey -Name "(default)" -Value $manifestPath

New-Item -Path $edgeKey -Force | Out-Null
Set-ItemProperty -Path $edgeKey -Name "(default)" -Value $manifestPath

Write-Host "Native host manifest installed:"
Write-Host $manifestPath
Write-Host "Host executable:"
Write-Host $resolvedHostPath
Write-Host "Allowed extension origins:"
$allOrigins | ForEach-Object { Write-Host "- $_" }
