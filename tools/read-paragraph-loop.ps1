param(
    [string]$TargetTitlePattern = 'WORK DETAILS\.doc - Google Docs - Google Chrome',
    [string[]]$TargetProcessNames = @('chrome','msedge'),
    [int]$TargetFocusSettleMilliseconds = 1200,
    [string]$RightSpeakTitlePattern = '^RightSpeak$',
    [string]$ExpectedText,
    [string]$ProjectDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [int]$Repeat = 1,
    [switch]$ClearLogsBeforeRun,
    [switch]$RestartAndBuildBetweenRuns
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ExpectedText)) {
    throw 'ExpectedText is required.'
}

$LogPath = Join-Path $env:LOCALAPPDATA 'RightSpeak\logs\rightspeak.log'
$AppExe = Join-Path $ProjectDir 'bin\Debug\net10.0-windows10.0.19041.0\RightSpeak.exe'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class ReadParagraphLoopNativeMethods
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

    [ReadParagraphLoopNativeMethods]::ShowWindow($Process.MainWindowHandle, 9) | Out-Null
    Start-Sleep -Milliseconds 150
    [ReadParagraphLoopNativeMethods]::SetForegroundWindow($Process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 500
}

function Get-RightSpeakWindowElement {
    $deadline = (Get-Date).AddSeconds(20)
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

    throw 'RightSpeak window was not found.'
}

function Invoke-Button {
    param(
        [Parameter(Mandatory)] [string]$ButtonName
    )

    $window = Get-RightSpeakWindowElement
    Focus-Window -Process $window.Process

    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $ButtonName)
    $button = $window.Element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    if (-not $button) {
        throw "$ButtonName button was not found in RightSpeak UI."
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

function Wait-ParagraphCompletion {
    param([datetime]$StartedAtUtc)

    $deadline = (Get-Date).ToUniversalTime().AddSeconds(30)
    do {
        $events = @(Read-LogEvents)
        $completed = $events |
            Where-Object {
                $_.eventName -eq 'paragraph_workflow_command_completed' -and
                ([datetime]$_.timestampUtc) -ge $StartedAtUtc
            } |
            Select-Object -Last 1

        if ($completed) {
            return $completed
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date).ToUniversalTime() -lt $deadline)

    throw 'Read Paragraph did not complete within timeout.'
}

function Get-LatestParagraphSummary {
    param([datetime]$StartedAtUtc)

    $events = @(Read-LogEvents) | Where-Object { ([datetime]$_.timestampUtc) -ge $StartedAtUtc }
    $result = $events | Where-Object { $_.eventName -eq 'focused_read_paragraph_retrieval_result' } | Select-Object -Last 1
    $success = $events | Where-Object { $_.eventName -eq 'paragraph_retrieval_success' } | Select-Object -Last 1
    $clipboard = $events | Where-Object { $_.eventName -eq 'paragraph_provider_clipboard_success' } | Select-Object -Last 1
    $rejects = @($events | Where-Object { $_.eventName -eq 'paragraph_retrieval_candidate_rejected' })
    $errors = @($events | Where-Object { $_.level -in @('WARN', 'ERROR') -or $_.eventName -match 'failed|error|exception' } | Select-Object -Last 20)

    $actualText = $null
    if ($null -ne $success -and $null -ne $success.data) {
        $actualText = $success.data.text
    }

    if ([string]::IsNullOrWhiteSpace($actualText) -and $null -ne $clipboard -and $null -ne $clipboard.data) {
        $actualText = $clipboard.data.text
    }

    [pscustomobject]@{
        Result = $result
        Success = $success
        Clipboard = $clipboard
        Rejects = $rejects
        Errors = $errors
        ActualText = $actualText
    }
}

function Restart-Build-Start-App {
    Get-Process RightSpeak -ErrorAction SilentlyContinue | Stop-Process -Force
    Push-Location $ProjectDir
    try {
        dotnet build
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

for ($iteration = 1; $iteration -le $Repeat; $iteration++) {
    Write-Host "=== Read Paragraph loop iteration $iteration / $Repeat ==="

    if ($ClearLogsBeforeRun -and (Test-Path $LogPath)) {
        Clear-Content $LogPath
    }

    $target = Get-WindowByTitlePattern -Pattern $TargetTitlePattern -ProcessNames $TargetProcessNames
    if (-not $target) {
        throw "Target window not found. Pattern: $TargetTitlePattern"
    }

    Write-Host "Focusing target: $($target.ProcessName) [$($target.Id)] $($target.MainWindowTitle)"
    Focus-Window -Process $target
    Start-Sleep -Milliseconds $TargetFocusSettleMilliseconds

    $startedAtUtc = (Get-Date).ToUniversalTime()
    Invoke-Button -ButtonName 'Read Paragraph'
    Wait-ParagraphCompletion -StartedAtUtc $startedAtUtc | Out-Null
    $summary = Get-LatestParagraphSummary -StartedAtUtc $startedAtUtc

    $actual = ''
    if ($null -ne $summary.ActualText) {
        $actual = [string]$summary.ActualText
    }
    $actual = $actual.Trim()
    $expected = $ExpectedText.Trim()
    $isMatch = -not [string]::IsNullOrWhiteSpace($actual) -and $actual -eq $expected

    Write-Host "Result source: $($summary.Result.data.source)"
    Write-Host "Preview: $($summary.Result.data.textPreview)"
    Write-Host "Reject count: $($summary.Rejects.Count)"
    Write-Host "Warnings/errors: $($summary.Errors.Count)"
    Write-Host "Exact match: $isMatch"

    if (-not $isMatch) {
        Write-Host "Actual text:"
        Write-Host $actual
        throw 'Read Paragraph result did not match expected paragraph text.'
    }

    if ($RestartAndBuildBetweenRuns -and $iteration -lt $Repeat) {
        Restart-Build-Start-App
        if (Test-Path $LogPath) {
            Clear-Content $LogPath
        }
    }
}
