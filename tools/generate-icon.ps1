Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$assetFolder = Join-Path $repoRoot 'Assets'
New-Item -ItemType Directory -Force -Path $assetFolder | Out-Null

function New-PngFrame([int]$pixelSize) {
    $bitmap = [System.Drawing.Bitmap]::new($pixelSize, $pixelSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.ScaleTransform($pixelSize / 256.0, $pixelSize / 256.0)

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(16, 16, 104, 104, 180, 90)
    $path.AddArc(136, 16, 104, 104, 270, 90)
    $path.AddArc(136, 136, 104, 104, 0, 90)
    $path.AddArc(16, 136, 104, 104, 90, 90)
    $path.CloseFigure()

    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(16, 16, 224, 224),
        [System.Drawing.Color]::FromArgb(255, 36, 107, 253),
        [System.Drawing.Color]::FromArgb(255, 20, 185, 132),
        40.0
    )
    $graphics.FillPath($gradient, $path)

    $wave = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(55, 137),
        [System.Drawing.PointF]::new(91, 137),
        [System.Drawing.PointF]::new(106, 98),
        [System.Drawing.PointF]::new(129, 176),
        [System.Drawing.PointF]::new(151, 77),
        [System.Drawing.PointF]::new(174, 137),
        [System.Drawing.PointF]::new(201, 137)
    )
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 14)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawLines($pen, $wave)

    $memory = [System.IO.MemoryStream]::new()
    $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $memory.ToArray()
    $memory.Dispose()
    $pen.Dispose()
    $gradient.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
    return ,$bytes
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @($sizes | ForEach-Object { [pscustomobject]@{ Size = $_; Png = (New-PngFrame $_) } })
[System.IO.File]::WriteAllBytes((Join-Path $assetFolder 'makrowerkstatt.png'), $frames[-1].Png)

$iconStream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($iconStream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$frames.Count)
$dataOffset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -eq 256) { [byte]0 } else { [byte]$frame.Size }
    $writer.Write($dimension)
    $writer.Write($dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Png.Length)
    $writer.Write([uint32]$dataOffset)
    $dataOffset += $frame.Png.Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Png) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assetFolder 'makrowerkstatt.ico'), $iconStream.ToArray())
$writer.Dispose()
$iconStream.Dispose()
Write-Host "Symbol erstellt: Assets\makrowerkstatt.ico (16 bis 256 Pixel)"
