param(
    [string]$TargetTitlePattern = 'ACA-22-67 DESCRIPTIVE STATISTICS\.pdf - Google Chrome',
    [string]$RightSpeakTitlePattern = '^RightSpeak$',
    [string]$ProjectDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$Configuration = 'Debug',
    [int]$Repeat = 1,
    [switch]$RestartAndBuildBetweenRuns,
    [switch]$ClearLogsBeforeRun,
    [bool]$StopOnPatchReview = $true,
    [int]$AnalyzeTimeoutSeconds = 15,
    [int]$AppStartupTimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LogPath = Join-Path $env:LOCALAPPDATA 'RightSpeak\logs\rightspeak.log'
$AppExe = Join-Path $ProjectDir 'bin\Debug\net10.0-windows10.0.19041.0\RightSpeak.exe'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class AnalyzeLoopNativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
'@

function Get-WindowByTitlePattern {
    param(
        [Parameter(Mandatory)] [string]$Pattern,
        [string[]]$ProcessNames = @()
    )

    $processes = Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle }
    if ($ProcessNames.Count -gt 0) {
        $processes = $processes | Where-Object { $ProcessNames -contains $_.ProcessName }
    }

    $processes |
        Where-Object { $_.MainWindowTitle -match $Pattern } |
        Sort-Object Id |
        Select-Object -First 1
}

function Focus-Window {
    param([Parameter(Mandatory)] [System.Diagnostics.Process]$Process)

    [AnalyzeLoopNativeMethods]::ShowWindow($Process.MainWindowHandle, 9) | Out-Null
    Start-Sleep -Milliseconds 150
    [AnalyzeLoopNativeMethods]::SetForegroundWindow($Process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 400
}

function Get-RightSpeakWindowElement {
    $deadline = (Get-Date).AddSeconds($AppStartupTimeoutSeconds)
    do {
        $process = Get-WindowByTitlePattern -Pattern $RightSpeakTitlePattern -ProcessNames @('RightSpeak')
        if ($process) {
            $element = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            if ($element) {
                return @{ Process = $process; Element = $element }
            }
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "RightSpeak window was not found within $AppStartupTimeoutSeconds seconds."
}

function Invoke-AnalyzeButton {
    $window = Get-RightSpeakWindowElement
    Focus-Window -Process $window.Process

    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        'Analyze')
    $button = $window.Element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    if (-not $button) {
        throw 'Analyze button was not found in RightSpeak UI Automation tree.'
    }

    $invokePattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invokePattern.Invoke()
}

function Read-LogEvents {
    if (-not (Test-Path $LogPath)) {
        return @()
    }

    Get-Content $LogPath | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { $null }
    } | Where-Object { $_ -ne $null }
}

function Wait-AnalyzeCompleted {
    param([datetime]$StartedAtUtc)

    $deadline = (Get-Date).ToUniversalTime().AddSeconds($AnalyzeTimeoutSeconds)
    do {
        $events = @(Read-LogEvents)
        $completed = $events |
            Where-Object {
                $_.eventName -eq 'analyze_external_app_completed' -and
                ([datetime]$_.timestampUtc) -ge $StartedAtUtc
            } |
            Select-Object -Last 1

        if ($completed) {
            return $completed.data.operationId
        }

        $failed = $events |
            Where-Object {
                $_.eventName -eq 'analyze_external_app_failed' -and
                ([datetime]$_.timestampUtc) -ge $StartedAtUtc
            } |
            Select-Object -Last 1

        if ($failed) {
            throw "Analyze failed: $($failed.data.message)"
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date).ToUniversalTime() -lt $deadline)

    throw "Analyze did not complete within $AnalyzeTimeoutSeconds seconds."
}

function Get-AnalyzeSummary {
    param([Parameter(Mandatory)] [string]$OperationId)

    $events = @(Read-LogEvents)
    $report = $events |
        Where-Object { $_.eventName -eq 'analyze_external_app_report' -and $_.data.operationId -eq $OperationId } |
        Select-Object -Last 1
    $core = $events |
        Where-Object { $_.eventName -eq 'analyze_external_app_main_context_core' -and $_.data.operationId -eq $OperationId } |
        Select-Object -Last 1
    $chunks = @($events |
        Where-Object { $_.eventName -eq 'analyze_external_app_main_context_chunk' -and $_.data.operationId -eq $OperationId })
    $errors = @($events |
        Where-Object { $_.level -in @('ERROR','WARN') -or $_.eventName -match 'failed|error|exception' } |
        Select-Object -Last 20)

    $suspiciousPattern = 'This PDF is inaccessible|Quick Response Code|How to cite|open access|Creative Commons|reprints@|Mishra, et al|J Paramed Sci|methods used to test the normality of\s+methods|Figure \d+:|Fig\. \d+:|Table \d+:'
    $suspiciousChunks = @($chunks | Where-Object {
        $_.data.includeByDefault -eq 'True' -and
        ($_.data.content -match $suspiciousPattern -or $_.data.contentPreview -match $suspiciousPattern)
    })

    [pscustomobject]@{
        OperationId = $OperationId
        ReportLines = if ($report) { @($report.data.report -split "`r?`n") } else { @() }
        Core = if ($core) { $core.data } else { $null }
        ChunkCount = $chunks.Count
        IncludedChunkCount = @($chunks | Where-Object { $_.data.includeByDefault -eq 'True' }).Count
        SuspiciousChunkCount = $suspiciousChunks.Count
        SuspiciousChunks = $suspiciousChunks
        ErrorCount = $errors.Count
        Errors = $errors
    }
}

function Write-AnalyzeSummary {
    param([Parameter(Mandatory)] $Summary)

    Write-Host "Analyze operation: $($Summary.OperationId)"
    if ($Summary.ReportLines.Count -gt 0) {
        Write-Host 'Report:'
        $Summary.ReportLines | Select-Object -First 12 | ForEach-Object { Write-Host "  $_" }
    }

    if ($Summary.Core) {
        Write-Host 'Core:'
        Write-Host "  candidate=$($Summary.Core.selectedCandidateName)"
        Write-Host "  mode=$($Summary.Core.extractionMode) pageType=$($Summary.Core.pageType)"
        Write-Host "  coreLength=$($Summary.Core.coreLength) keptLines=$($Summary.Core.keptLineCount) noiseLines=$($Summary.Core.noiseLineCount) chunks=$($Summary.Core.chunkCount)"
    }

    Write-Host "Included chunks: $($Summary.IncludedChunkCount)"
    Write-Host "Suspicious included chunks: $($Summary.SuspiciousChunkCount)"
    foreach ($chunk in $Summary.SuspiciousChunks | Select-Object -First 8) {
        Write-Host "  [$($chunk.data.chunkIndex)] $($chunk.data.sectionType) include=$($chunk.data.includeByDefault) preview=$($chunk.data.contentPreview)"
    }

    Write-Host "Recent warning/error events: $($Summary.ErrorCount)"
    foreach ($errorEvent in $Summary.Errors | Select-Object -First 5) {
        Write-Host "  $($errorEvent.timestampUtc) $($errorEvent.level) $($errorEvent.eventName)"
    }

    if ($Summary.SuspiciousChunkCount -gt 0 -or $Summary.ErrorCount -gt 0) {
        Write-Host 'Needs patch review: YES'
    }
    else {
        Write-Host 'Needs patch review: no obvious issue from scripted checks'
    }
}

function Get-SummaryDigest {
    param([Parameter(Mandatory)] $Summary)

    $core = $Summary.Core
    if (-not $core) {
        return "no-core:$($Summary.OperationId)"
    }

    return @(
        $core.selectedClusterHash,
        $core.selectedCandidateName,
        $core.extractionMode,
        $core.coreLength,
        $core.keptLineCount,
        $core.noiseLineCount,
        $core.chunkCount,
        $Summary.SuspiciousChunkCount,
        $Summary.ErrorCount
    ) -join '|'
}

function Restart-Build-Start-App {
    Get-Process RightSpeak -ErrorAction SilentlyContinue | Stop-Process -Force
    Push-Location $ProjectDir
    try {
        dotnet build -c $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path $AppExe)) {
        throw "RightSpeak executable was not found: $AppExe"
    }

    Start-Process -FilePath $AppExe -WorkingDirectory (Split-Path $AppExe)
    Get-RightSpeakWindowElement | Out-Null
}

$previousOperationId = $null
$previousDigest = $null

for ($iteration = 1; $iteration -le $Repeat; $iteration++) {
    Write-Host "=== Analyze loop iteration $iteration / $Repeat ==="

    if ($ClearLogsBeforeRun -and (Test-Path $LogPath)) {
        Clear-Content $LogPath
    }

    $target = Get-WindowByTitlePattern -Pattern $TargetTitlePattern -ProcessNames @('chrome','msedge')
    if (-not $target) {
        throw "Target window not found. Pattern: $TargetTitlePattern"
    }

    Write-Host "Focusing target: $($target.ProcessName) [$($target.Id)] $($target.MainWindowTitle)"
    Focus-Window -Process $target

    $startedAtUtc = (Get-Date).ToUniversalTime()
    Invoke-AnalyzeButton
    $operationId = Wait-AnalyzeCompleted -StartedAtUtc $startedAtUtc
    if ($previousOperationId -and $operationId -eq $previousOperationId) {
        throw "Analyze loop did not advance: operation id repeated ($operationId)."
    }

    $summary = Get-AnalyzeSummary -OperationId $operationId
    Write-AnalyzeSummary -Summary $summary

    $digest = Get-SummaryDigest -Summary $summary
    if ($previousDigest -and $digest -eq $previousDigest) {
        throw 'Analyze loop did not advance: latest analysis digest is identical to the previous iteration.'
    }

    $previousOperationId = $operationId
    $previousDigest = $digest

    if ($StopOnPatchReview -and ($summary.SuspiciousChunkCount -gt 0 -or $summary.ErrorCount -gt 0)) {
        throw 'Analyze loop stopped because the latest result needs patch review.'
    }

    if ($RestartAndBuildBetweenRuns -and $iteration -lt $Repeat) {
        Write-Host 'Restart/build/start requested before next iteration.'
        Restart-Build-Start-App
        if (Test-Path $LogPath) {
            Clear-Content $LogPath
        }
    }
}
