param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "RightSpeak.ico"),
    [string]$PreviewPath = (Join-Path $PSScriptRoot "RightSpeak.preview.png")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$Cyan = [System.Drawing.Color]::FromArgb(0x36, 0xD8, 0xFF)
$MidBlue = [System.Drawing.Color]::FromArgb(0x1F, 0x8C, 0xFF)
$DeepBlue = [System.Drawing.Color]::FromArgb(0x0C, 0x47, 0xFF)
$WhiteGlow = [System.Drawing.Color]::FromArgb(0xCC, 0xFF, 0xFF, 0xFF)

function New-RoundedRectPath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $diameter = [Math]::Min($Radius, [Math]::Min($Width, $Height))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.StartFigure()
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-SpeechBubblePath {
    param([float]$Scale)

    $x = 22 * $Scale
    $y = 34 * $Scale
    $width = 158 * $Scale
    $height = 126 * $Scale
    $radius = 28 * $Scale
    $bottom = $y + $height
    $right = $x + $width

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.StartFigure()
    $path.AddArc($x, $y, $radius, $radius, 180, 90)
    $path.AddLine($x + ($radius / 2), $y, $right - ($radius / 2), $y)
    $path.AddArc($right - $radius, $y, $radius, $radius, 270, 74)
    $path.AddLine($right, $y + ($radius / 2), $right, $bottom - ($radius * 1.25))
    $path.AddLine($right, $bottom - ($radius * 1.25), $right - ($radius * 0.3), $bottom - ($radius * 1.2))
    $path.AddLine($right - ($radius * 0.3), $bottom - ($radius * 1.2), $right - ($radius * 0.3), $bottom - ($radius * 0.9))
    $path.AddArc($right - $radius, $bottom - $radius, $radius, $radius, 0, 90)
    $path.AddLine($right - ($radius / 2), $bottom, $x + (62 * $Scale), $bottom)
    $path.AddLine($x + (62 * $Scale), $bottom, $x + (18 * $Scale), $bottom + (30 * $Scale))
    $path.AddLine($x + (18 * $Scale), $bottom + (30 * $Scale), $x + (28 * $Scale), $bottom)
    $path.AddLine($x + (28 * $Scale), $bottom, $x + ($radius / 2), $bottom)
    $path.AddArc($x, $bottom - $radius, $radius, $radius, 90, 90)
    $path.AddLine($x, $bottom - ($radius / 2), $x, $y + ($radius / 2))
    $path.CloseFigure()
    return $path
}

function Draw-RoundedLine {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush,
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height
    )

    $path = New-RoundedRectPath -X $X -Y $Y -Width $Width -Height $Height -Radius $Height
    try {
        $Graphics.FillPath($Brush, $path)
    }
    finally {
        $path.Dispose()
    }
}

function Draw-SmallIcon {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$Size
    )

    $scale = $Size / 256.0
    $stroke = [Math]::Max(1.6, 20 * $scale)
    $waveStroke = [Math]::Max(1.4, 14 * $scale)

    $glowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(36, 54, 216, 255))
    try {
        $Graphics.FillEllipse($glowBrush, 3 * $scale, 4 * $scale, 250 * $scale, 248 * $scale)
    }
    finally {
        $glowBrush.Dispose()
    }

    $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF (82 * $scale), (74 * $scale)),
        (New-Object System.Drawing.PointF (170 * $scale), (176 * $scale)),
        $MidBlue,
        $DeepBlue)

    $bodyPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    try {
        $speakerLeft = 74 * $scale
        $speakerTop = 88 * $scale
        $speakerWidth = 118 * $scale
        $speakerHeight = 86 * $scale
        $hornRight = 206 * $scale
        $hornTop = 62 * $scale
        $hornBottom = 198 * $scale

        $bodyPath.StartFigure()
        $bodyPath.AddArc($speakerLeft, $speakerTop, 24 * $scale, 24 * $scale, 90, 180)
        $bodyPath.AddLine($speakerLeft + (12 * $scale), $speakerTop, $speakerLeft + (60 * $scale), $speakerTop)
        $bodyPath.AddLine($speakerLeft + (60 * $scale), $speakerTop, $hornRight, $hornTop)
        $bodyPath.AddLine($hornRight, $hornTop, $hornRight, $hornBottom)
        $bodyPath.AddLine($hornRight, $hornBottom, $speakerLeft + (60 * $scale), $speakerTop + $speakerHeight)
        $bodyPath.AddLine($speakerLeft + (60 * $scale), $speakerTop + $speakerHeight, $speakerLeft + (12 * $scale), $speakerTop + $speakerHeight)
        $bodyPath.AddArc($speakerLeft, $speakerTop + $speakerHeight - (24 * $scale), 24 * $scale, 24 * $scale, 90, 180)
        $bodyPath.CloseFigure()

        $Graphics.FillPath($bodyBrush, $bodyPath)

        $outlinePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $stroke
        try {
            $outlinePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $outlinePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $outlinePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $Graphics.DrawPath($outlinePen, $bodyPath)
        }
        finally {
            $outlinePen.Dispose()
        }
    }
    finally {
        $bodyBrush.Dispose()
        $bodyPath.Dispose()
    }

    $wavePen = New-Object System.Drawing.Pen $Cyan, $waveStroke
    try {
        $wavePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $wavePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $wavePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $Graphics.DrawArc($wavePen, 172 * $scale, 82 * $scale, 42 * $scale, 98 * $scale, -56, 112)
        $Graphics.DrawArc($wavePen, 188 * $scale, 64 * $scale, 46 * $scale, 134 * $scale, -52, 104)
    }
    finally {
        $wavePen.Dispose()
    }
}

function Draw-Speaker {
    param(
        [System.Drawing.Graphics]$Graphics,
        [float]$Scale,
        [int]$Size
    )

    $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF (126 * $Scale), (108 * $Scale)),
        (New-Object System.Drawing.PointF (188 * $Scale), (188 * $Scale)),
        $MidBlue,
        $DeepBlue)
    $bodyPath = New-Object System.Drawing.Drawing2D.GraphicsPath

    try {
        $rectPath = New-RoundedRectPath -X (128 * $Scale) -Y (118 * $Scale) -Width (34 * $Scale) -Height (46 * $Scale) -Radius (12 * $Scale)
        try {
            $bodyPath.AddPath($rectPath, $false)
        }
        finally {
            $rectPath.Dispose()
        }

        $bodyPath.StartFigure()
        $bodyPath.AddLine(162 * $Scale, 118 * $Scale, 199 * $Scale, 93 * $Scale)
        $bodyPath.AddLine(199 * $Scale, 93 * $Scale, 199 * $Scale, 189 * $Scale)
        $bodyPath.AddLine(199 * $Scale, 189 * $Scale, 162 * $Scale, 164 * $Scale)
        $bodyPath.CloseFigure()

        $Graphics.FillPath($bodyBrush, $bodyPath)

        $outlinePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(2.6, 12 * $Scale))
        try {
            $outlinePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $Graphics.DrawPath($outlinePen, $bodyPath)
        }
        finally {
            $outlinePen.Dispose()
        }
    }
    finally {
        $bodyBrush.Dispose()
        $bodyPath.Dispose()
    }

    $centerPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(210, 255, 255, 255)), ([Math]::Max(1.2, 5 * $Scale))
    try {
        $Graphics.DrawArc($centerPen, 195 * $Scale, 128 * $Scale, 20 * $Scale, 28 * $Scale, -72, 144)
    }
    finally {
        $centerPen.Dispose()
    }

    $wavePen = New-Object System.Drawing.Pen $Cyan, ([Math]::Max(1.8, 8 * $Scale))
    try {
        $wavePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $wavePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $Graphics.DrawArc($wavePen, 204 * $Scale, 112 * $Scale, 28 * $Scale, 52 * $Scale, -60, 120)
        $Graphics.DrawArc($wavePen, 214 * $Scale, 98 * $Scale, 34 * $Scale, 80 * $Scale, -54, 108)
    }
    finally {
        $wavePen.Dispose()
    }

    if ($Size -ge 128) {
        $highlightPen = New-Object System.Drawing.Pen $WhiteGlow, ([Math]::Max(1.2, 4 * $Scale))
        try {
            $Graphics.DrawArc($highlightPen, 117 * $Scale, 106 * $Scale, 94 * $Scale, 92 * $Scale, 210, 90)
        }
        finally {
            $highlightPen.Dispose()
        }
    }
}

function Draw-FullIcon {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$Size
    )

    $scale = $Size / 256.0

    if ($Size -ge 64) {
        $bubbleGlow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(28, 54, 216, 255))
        $speakerGlow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(54, 255, 255, 255))
        try {
            $Graphics.FillEllipse($bubbleGlow, 8 * $scale, 16 * $scale, 172 * $scale, 170 * $scale)
            $Graphics.FillEllipse($speakerGlow, 124 * $scale, 100 * $scale, 92 * $scale, 92 * $scale)
        }
        finally {
            $bubbleGlow.Dispose()
            $speakerGlow.Dispose()
        }
    }

    $bubblePath = New-SpeechBubblePath -Scale $scale
    $bubbleBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF (24 * $scale), (38 * $scale)),
        (New-Object System.Drawing.PointF (172 * $scale), (214 * $scale)),
        $Cyan,
        $DeepBlue)
    $bubblePen = New-Object System.Drawing.Pen $bubbleBrush, ([Math]::Max(2.8, 13 * $scale))

    try {
        $bubblePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $bubblePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $bubblePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $Graphics.DrawPath($bubblePen, $bubblePath)
    }
    finally {
        $bubblePen.Dispose()
        $bubbleBrush.Dispose()
        $bubblePath.Dispose()
    }

    $lineBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF (40 * $scale), (56 * $scale)),
        (New-Object System.Drawing.PointF (150 * $scale), (120 * $scale)),
        ([System.Drawing.Color]::FromArgb(220, 90, 205, 255)),
        ([System.Drawing.Color]::FromArgb(220, 30, 123, 255)))

    try {
        Draw-RoundedLine -Graphics $Graphics -Brush $lineBrush -X (54 * $scale) -Y (80 * $scale) -Width (100 * $scale) -Height (12 * $scale)
        Draw-RoundedLine -Graphics $Graphics -Brush $lineBrush -X (54 * $scale) -Y (112 * $scale) -Width (74 * $scale) -Height (12 * $scale)
        if ($Size -ge 48) {
            Draw-RoundedLine -Graphics $Graphics -Brush $lineBrush -X (54 * $scale) -Y (144 * $scale) -Width (44 * $scale) -Height (12 * $scale)
        }
    }
    finally {
        $lineBrush.Dispose()
    }

    Draw-Speaker -Graphics $Graphics -Scale $scale -Size $Size
}

function New-FrameBitmap {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        if ($Size -le 24) {
            Draw-SmallIcon -Graphics $graphics -Size $Size
        }
        else {
            Draw-FullIcon -Graphics $graphics -Size $Size
        }
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output -NoEnumerate ([byte[]]$stream.ToArray())
    }
    finally {
        $stream.Dispose()
    }
}

function Write-IconFile {
    param(
        [string]$Path,
        [System.Collections.IList]$Frames
    )

    $pngFrames = @()
    foreach ($frame in $Frames) {
        $pngFrames += ,(Get-PngBytes -Bitmap $frame)
    }

    $file = [System.IO.File]::Create($Path)
    $writer = New-Object System.IO.BinaryWriter $file

    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$pngFrames.Count)

        $offset = 6 + (16 * $pngFrames.Count)
        for ($index = 0; $index -lt $Frames.Count; $index++) {
            $frame = $Frames[$index]
            $png = $pngFrames[$index]

            $writer.Write([byte]($(if ($frame.Width -ge 256) { 0 } else { $frame.Width })))
            $writer.Write([byte]($(if ($frame.Height -ge 256) { 0 } else { $frame.Height })))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([int]$png.Length)
            $writer.Write([int]$offset)
            $offset += $png.Length
        }

        foreach ($png in $pngFrames) {
            $file.Write([byte[]]$png, 0, $png.Length)
        }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }
}

$frames = New-Object System.Collections.ArrayList
try {
    foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
        [void]$frames.Add((New-FrameBitmap -Size $size))
    }

    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
    Write-IconFile -Path $OutputPath -Frames $frames
    $frames[$frames.Count - 1].Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    foreach ($frame in $frames) {
        $frame.Dispose()
    }
}
