param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "Results")
)

$ErrorActionPreference = "Stop"

$allowedStatuses = @("Pass", "Fail", "Blocked", "Not Run")

$checks = @(
    @{ Area = "Retrieval Matrix"; Check = "RightSpeak input box - Read typed text" },
    @{ Area = "Retrieval Matrix"; Check = "RightSpeak input box - Read selected text" },
    @{ Area = "Retrieval Matrix"; Check = "Windows Notepad - Read selected text" },
    @{ Area = "Retrieval Matrix"; Check = "Windows Notepad - Read paragraph" },
    @{ Area = "Retrieval Matrix"; Check = "Windows Notepad - Read document" },
    @{ Area = "Retrieval Matrix"; Check = "VS Code editor - Read selected text" },
    @{ Area = "Retrieval Matrix"; Check = "VS Code editor - Read paragraph" },
    @{ Area = "Retrieval Matrix"; Check = "VS Code editor - Read document" },
    @{ Area = "Retrieval Matrix"; Check = "Edge/Chrome text field - Read selected text" },
    @{ Area = "Retrieval Matrix"; Check = "Edge/Chrome text field - Read paragraph" },
    @{ Area = "Retrieval Matrix"; Check = "Edge/Chrome page selection - Read selected text" },
    @{ Area = "Retrieval Matrix"; Check = "Edge/Chrome page selection - Context menu read" },

    @{ Area = "Hotkey/Tray"; Check = "Startup hotkey registration" },
    @{ Area = "Hotkey/Tray"; Check = "Apply new hotkeys in window" },
    @{ Area = "Hotkey/Tray"; Check = "Tray labels reflect hotkeys" },
    @{ Area = "Hotkey/Tray"; Check = "Hide to tray then restore" },
    @{ Area = "Hotkey/Tray"; Check = "Single-instance activation" },

    @{ Area = "Settings"; Check = "Save voice/rate/hotkeys and restart" },
    @{ Area = "Settings"; Check = "Missing settings file recovery" },
    @{ Area = "Settings"; Check = "Malformed settings file recovery" },

    @{ Area = "Installer/First-Run"; Check = "Install browser integration (single ID)" },
    @{ Area = "Installer/First-Run"; Check = "Install browser integration (Chrome + Edge IDs)" },
    @{ Area = "Installer/First-Run"; Check = "-RunBridgeTest path" },
    @{ Area = "Installer/First-Run"; Check = "Troubleshooting doc followability" },

    @{ Area = "Diagnostics"; Check = "Success/failure events written to diagnostics log" }
)

function Prompt-Status {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt
    )

    while ($true) {
        $value = Read-Host "$Prompt [Pass/Fail/Blocked/Not Run]"
        if ([string]::IsNullOrWhiteSpace($value)) {
            return "Not Run"
        }

        $normalized = $value.Trim()
        foreach ($allowed in $allowedStatuses) {
            if ($normalized.Equals($allowed, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $allowed
            }
        }

        Write-Host "Invalid status. Allowed: Pass, Fail, Blocked, Not Run"
    }
}

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}

$sessionId = Get-Date -Format "yyyyMMdd-HHmmss"
$results = @()

Write-Host ""
Write-Host "RightSpeak RC Manual Checklist"
Write-Host "Session: $sessionId"
Write-Host "Press Enter on status to default to 'Not Run'."
Write-Host ""

foreach ($check in $checks) {
    Write-Host "Area: $($check.Area)"
    Write-Host "Check: $($check.Check)"
    $status = Prompt-Status -Prompt "Status"
    $notes = Read-Host "Notes (optional)"
    Write-Host ""

    $results += [pscustomobject]@{
        Area = $check.Area
        Check = $check.Check
        Status = $status
        Notes = $notes
    }
}

$jsonPath = Join-Path $OutputDirectory "RC-Manual-Results-$sessionId.json"
$mdPath = Join-Path $OutputDirectory "RC-Manual-Results-$sessionId.md"

$results | ConvertTo-Json -Depth 5 | Set-Content -Path $jsonPath -Encoding UTF8

$lines = @()
$lines += "# RightSpeak RC Manual Results"
$lines += ""
$lines += "Session: $sessionId"
$lines += ""
$lines += "| Area | Check | Status | Notes |"
$lines += "|---|---|---|---|"

foreach ($item in $results) {
    $notes = ($item.Notes -replace '\|', '/')
    $lines += "| $($item.Area) | $($item.Check) | $($item.Status) | $notes |"
}

$lines += ""
$passCount = ($results | Where-Object { $_.Status -eq "Pass" }).Count
$failCount = ($results | Where-Object { $_.Status -eq "Fail" }).Count
$blockedCount = ($results | Where-Object { $_.Status -eq "Blocked" }).Count
$notRunCount = ($results | Where-Object { $_.Status -eq "Not Run" }).Count
$lines += "Summary:"
$lines += "- Pass: $passCount"
$lines += "- Fail: $failCount"
$lines += "- Blocked: $blockedCount"
$lines += "- Not Run: $notRunCount"

$lines | Set-Content -Path $mdPath -Encoding UTF8

Write-Host "Saved:"
Write-Host "- $jsonPath"
Write-Host "- $mdPath"
Write-Host ""
Write-Host "Tip: copy results into Resources/QA/RC-Regression-Execution.md"
