# Rebuild the multi-resolution quota gauge icon; no external graphics tools needed.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$target = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets/app.ico'
$frames = @()
foreach ($size in @(16, 20, 24, 32, 48, 64, 128, 256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform(($size / 256.0), ($size / 256.0))
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#182432'))
    $track = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#40566B'), 24)
    $accent = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#4EC9B0'), 24)
    $needle = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#FFFFFF'), 16)
    $dot = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#FFFFFF'))
    $accent.StartCap = $accent.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $needle.StartCap = $needle.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    try {
        $graphics.FillEllipse($background, 4, 4, 248, 248)
        $graphics.DrawArc($track, 46, 46, 164, 164, 135, 270)
        $graphics.DrawArc($accent, 46, 46, 164, 164, 135, 195)
        $graphics.DrawLine($needle, 128, 128, 168, 89)
        $graphics.FillEllipse($dot, 113, 113, 30, 30)
        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += ,@{ Size = $size; Bytes = $stream.ToArray() }
            if ($size -eq 256) { $bitmap.Save((Join-Path (Split-Path $target) 'app.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
        } finally { $stream.Dispose() }
    } finally {
        $graphics.Dispose(); $bitmap.Dispose(); $background.Dispose(); $track.Dispose(); $accent.Dispose(); $needle.Dispose(); $dot.Dispose()
    }
}
# ICO entries point to PNG frames; keeping all sizes avoids blurry tray scaling.
$file = [System.IO.File]::Create($target)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output "Created $target"
