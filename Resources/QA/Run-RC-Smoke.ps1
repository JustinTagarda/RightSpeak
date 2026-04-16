param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$Framework = "net10.0-windows",
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"

$results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param(
        [Parameter(Mandatory = $true)][string]$Check,
        [Parameter(Mandatory = $true)][string]$Status,
        [Parameter(Mandatory = $true)][string]$Details
    )

    $results.Add([pscustomobject]@{
        Check = $Check
        Status = $Status
        Details = $Details
    }) | Out-Null
}

function Run-Check {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    try {
        & $Action
        Add-Result -Check $Name -Status "Pass" -Details "OK"
    }
    catch {
        Add-Result -Check $Name -Status "Fail" -Details $_.Exception.Message
    }
}

Run-Check -Name "Build verify output" -Action {
    $buildOutputPath = Join-Path $ProjectRoot "bin\$Configuration\$Framework-verify\"
    dotnet build (Join-Path $ProjectRoot "RightSpeak.csproj") "-p:OutputPath=$buildOutputPath" | Out-Null
}

Run-Check -Name "Install-NativeHost.ps1 parse" -Action {
    [void][scriptblock]::Create((Get-Content -Raw -Path (Join-Path $ProjectRoot "Resources\BrowserIntegration\Install-NativeHost.ps1")))
}

Run-Check -Name "Install-BrowserExtensionIntegration.ps1 parse" -Action {
    [void][scriptblock]::Create((Get-Content -Raw -Path (Join-Path $ProjectRoot "Resources\BrowserIntegration\Install-BrowserExtensionIntegration.ps1")))
}

Run-Check -Name "Test-BrowserIntegration.ps1 parse" -Action {
    [void][scriptblock]::Create((Get-Content -Raw -Path (Join-Path $ProjectRoot "Resources\BrowserIntegration\Test-BrowserIntegration.ps1")))
}

Run-Check -Name "Diagnostics path writable" -Action {
    $logDirectory = Join-Path $env:LOCALAPPDATA "RightSpeak\logs"
    if (-not (Test-Path $logDirectory)) {
        New-Item -ItemType Directory -Path $logDirectory | Out-Null
    }

    $probePath = Join-Path $logDirectory "rc-smoke-probe.tmp"
    "probe" | Set-Content -Path $probePath -Encoding UTF8
    Remove-Item -Path $probePath -Force
}

$results | Format-Table -AutoSize

$failures = $results | Where-Object { $_.Status -eq "Fail" }
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "RC smoke checks completed with failures."
    exit 1
}

Write-Host ""
Write-Host "RC smoke checks passed."
exit 0
