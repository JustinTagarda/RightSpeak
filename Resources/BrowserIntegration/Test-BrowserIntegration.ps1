param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$Framework = "net10.0-windows",
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$TestText = "RightSpeak browser integration test text."
)

$ErrorActionPreference = "Stop"

function Write-NativeMessage {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)]
        [string]$Payload
    )

    $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($Payload)
    $lengthBytes = [System.BitConverter]::GetBytes($payloadBytes.Length)
    $Stream.Write($lengthBytes, 0, $lengthBytes.Length)
    $Stream.Write($payloadBytes, 0, $payloadBytes.Length)
    $Stream.Flush()
}

function Read-ExactBytes {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)]
        [int]$Count
    )

    $buffer = New-Object byte[] $Count
    $offset = 0

    while ($offset -lt $Count) {
        $read = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($read -le 0) {
            throw "Stream closed before expected data was received."
        }

        $offset += $read
    }

    return $buffer
}

function Read-NativeMessage {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Stream
    )

    $lengthBytes = Read-ExactBytes -Stream $Stream -Count 4
    $payloadLength = [System.BitConverter]::ToInt32($lengthBytes, 0)
    if ($payloadLength -le 0) {
        throw "Invalid native message length: $payloadLength"
    }

    $payloadBytes = Read-ExactBytes -Stream $Stream -Count $payloadLength
    return [System.Text.Encoding]::UTF8.GetString($payloadBytes)
}

$exePath = Join-Path $ProjectRoot "bin\$Configuration\$Framework\RightSpeak.exe"
$nativeHostExePath = Join-Path $ProjectRoot "RightSpeak.NativeHost\bin\$Configuration\$Framework\RightSpeak.NativeHost.exe"
if (-not (Test-Path $exePath)) {
    throw "RightSpeak executable not found: $exePath. Build the app first."
}
if (-not (Test-Path $nativeHostExePath)) {
    throw "RightSpeak native host executable not found: $nativeHostExePath. Build the solution first."
}

$existingApp = Get-Process -Name RightSpeak -ErrorAction SilentlyContinue | Select-Object -First 1
$startedApp = $null

if (-not $existingApp) {
    $startedApp = Start-Process -FilePath $exePath -PassThru
    Start-Sleep -Milliseconds 1200
}

try {
    $sendTextProcess = Start-Process -FilePath $exePath -ArgumentList @("--send-text", $TestText) -PassThru -Wait
    if ($sendTextProcess.ExitCode -ne 0) {
        throw "--send-text verification failed with exit code $($sendTextProcess.ExitCode)."
    }

    $nativeHostStartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $nativeHostStartInfo.FileName = $nativeHostExePath
    $nativeHostStartInfo.UseShellExecute = $false
    $nativeHostStartInfo.RedirectStandardInput = $true
    $nativeHostStartInfo.RedirectStandardOutput = $true
    $nativeHostStartInfo.RedirectStandardError = $true

    $nativeHostProcess = New-Object System.Diagnostics.Process
    $nativeHostProcess.StartInfo = $nativeHostStartInfo
    $null = $nativeHostProcess.Start()

    try {
        $payload = @{ text = $TestText } | ConvertTo-Json -Compress
        Write-NativeMessage -Stream $nativeHostProcess.StandardInput.BaseStream -Payload $payload
        $nativeHostProcess.StandardInput.Close()

        $responseJson = Read-NativeMessage -Stream $nativeHostProcess.StandardOutput.BaseStream
        $response = $responseJson | ConvertFrom-Json

        if (-not $response.success) {
            throw "Native host verification failed: $($response.message)"
        }
    }
    finally {
        if (-not $nativeHostProcess.HasExited) {
            $nativeHostProcess.Kill()
            $nativeHostProcess.WaitForExit()
        }
        $nativeHostProcess.Dispose()
    }

    Write-Host "Browser integration bridge verification succeeded."
    Write-Host "Validated:"
    Write-Host "- RightSpeak.exe --send-text"
    Write-Host "- RightSpeak.NativeHost.exe"
}
finally {
    if ($startedApp) {
        if (-not $startedApp.HasExited) {
            $startedApp.CloseMainWindow() | Out-Null
            Start-Sleep -Milliseconds 300
        }

        if (-not $startedApp.HasExited) {
            $startedApp.Kill()
        }
    }
}
