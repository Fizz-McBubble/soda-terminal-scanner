[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SourcePng,
    [Parameter(Mandatory=$true)][string]$OutputIco
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$source = [Drawing.Bitmap]::new([IO.Path]::GetFullPath($SourcePng))
$frames = @()
try {
    foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source, [Drawing.Rectangle]::new(0, 0, $size, $size))
            $stream = [IO.MemoryStream]::new()
            $writer = [IO.BinaryWriter]::new($stream)
            # ICO DIB: straight BGRA pixels and a real 1-bit transparency mask.
            # Avoid Icon.Save/GetHicon, which can serialize a blank 4-bit palette.
            $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
            $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([int]0); $writer.Write([int]($size * $size * 4 + $maskStride * $size))
            1..4 | ForEach-Object { $writer.Write([int]0) }
            for ($y = $size - 1; $y -ge 0; $y--) {
                for ($x = 0; $x -lt $size; $x++) {
                    $pixel = $bitmap.GetPixel($x, $y)
                    $writer.Write([byte]$pixel.B); $writer.Write([byte]$pixel.G)
                    $writer.Write([byte]$pixel.R); $writer.Write([byte]$pixel.A)
                }
            }
            for ($y = $size - 1; $y -ge 0; $y--) {
                $mask = [byte[]]::new($maskStride)
                for ($x = 0; $x -lt $size; $x++) {
                    if ($bitmap.GetPixel($x, $y).A -eq 0) {
                        $index = [int][Math]::Floor($x / 8)
                        $mask[$index] = $mask[$index] -bor (128 -shr ($x % 8))
                    }
                }
                $writer.Write($mask)
            }
            $frames += @{ Size = $size; Bytes = $stream.ToArray() }
            $writer.Dispose(); $stream.Dispose()
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
    $output = [IO.File]::Create([IO.Path]::GetFullPath($OutputIco))
    $writer = [IO.BinaryWriter]::new($output)
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
    } finally { $writer.Dispose(); $output.Dispose() }
} finally { $source.Dispose() }
