# Generates src/BingWallpaperUpdater.App/Resources/tray.ico - a multi-size stock tray icon
# (filled rounded square with a white "B" glyph) at 16, 20, 24, 32 and 48 px.
#
# Each frame is PNG-encoded and stored in a standard ICO container (6-byte header + one
# 16-byte directory entry per image + PNG payloads), which Windows accepts since Vista.
# Run once from the repo root; commit the generated .ico. Phase 3 may replace the artwork.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/make-icon.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$outPath  = Join-Path $repoRoot 'src\BingWallpaperUpdater.App\Resources\tray.ico'
$sizes    = @(16, 20, 24, 32, 48)

function New-FramePng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.Clear([System.Drawing.Color]::Transparent)

        # Rounded square background (Bing-ish teal).
        $inset  = [Math]::Max(1, [int][Math]::Round($size * 0.04))
        $radius = [Math]::Max(2, [int][Math]::Round($size * 0.28))
        $rect   = New-Object System.Drawing.Rectangle $inset, $inset, ($size - 2 * $inset), ($size - 2 * $inset)
        $path   = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
        $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
        $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
        $path.CloseFigure()
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 120, 140))
        $g.FillPath($brush, $path)
        $brush.Dispose(); $path.Dispose()

        # White "B" glyph centred in the square.
        $fontSize = [single]($size * 0.62)
        $font = New-Object System.Drawing.Font 'Segoe UI', $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $fmt  = New-Object System.Drawing.StringFormat
        $fmt.Alignment     = [System.Drawing.StringAlignment]::Center
        $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
        $white = [System.Drawing.Brushes]::White
        $g.DrawString('B', $font, $white, (New-Object System.Drawing.RectangleF 0, 0, $size, $size), $fmt)
        $font.Dispose(); $fmt.Dispose()
    }
    finally { $g.Dispose() }

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()   # leading comma keeps the byte[] intact through the pipeline
}

$frames = @()
foreach ($s in $sizes) { $frames += ,@{ Size = $s; Png = [byte[]](New-FramePng $s) } }

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null
$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter $fs
try {
    # ICONDIR
    $bw.Write([uint16]0)               # reserved
    $bw.Write([uint16]1)               # type: 1 = icon
    $bw.Write([uint16]$frames.Count)   # image count

    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $bw.Write([byte]$dim)          # width  (0 = 256)
        $bw.Write([byte]$dim)          # height (0 = 256)
        $bw.Write([byte]0)             # colour count (0 = no palette)
        $bw.Write([byte]0)             # reserved
        $bw.Write([uint16]1)           # colour planes
        $bw.Write([uint16]32)          # bits per pixel
        $bw.Write([uint32]$f.Png.Length)
        $bw.Write([uint32]$offset)
        $offset += $f.Png.Length
    }
    foreach ($f in $frames) { $bw.Write([byte[]]$f.Png) }
}
finally { $bw.Dispose(); $fs.Dispose() }

Write-Host ("Wrote {0} ({1} bytes)" -f $outPath, (Get-Item $outPath).Length)
foreach ($f in $frames) { Write-Host ("  {0,3}x{0,-3} PNG {1,6} bytes" -f $f.Size, $f.Png.Length) }
