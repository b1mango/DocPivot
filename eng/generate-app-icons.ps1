[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\DocPivot.App\Assets'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

function New-DocPivotIconPng {
    param(
        [Parameter(Mandatory)]
        [int] $Size,
        [Parameter(Mandatory)]
        [string] $Background,
        [Parameter(Mandatory)]
        [string] $Foreground
    )

    $scale = $Size / 1024.0
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $backgroundBrush = [System.Windows.Media.SolidColorBrush]::new(
            [System.Windows.Media.ColorConverter]::ConvertFromString($Background))
        $foregroundBrush = [System.Windows.Media.SolidColorBrush]::new(
            [System.Windows.Media.ColorConverter]::ConvertFromString($Foreground))
        $borderColor = [System.Windows.Media.ColorConverter]::ConvertFromString($Foreground)
        $borderColor.A = 72
        $borderBrush = [System.Windows.Media.SolidColorBrush]::new($borderColor)
        $borderPen = [System.Windows.Media.Pen]::new($borderBrush, [Math]::Max(1.0, 8.0 * $scale))
        $bounds = [System.Windows.Rect]::new(44.0 * $scale, 44.0 * $scale, 936.0 * $scale, 936.0 * $scale)
        $radius = 188.0 * $scale
        $context.DrawRoundedRectangle($backgroundBrush, $borderPen, $bounds, $radius, $radius)

        $typeface = [System.Windows.Media.Typeface]::new(
            [System.Windows.Media.FontFamily]::new('Microsoft YaHei UI'),
            [System.Windows.FontStyles]::Normal,
            [System.Windows.FontWeights]::SemiBold,
            [System.Windows.FontStretches]::Normal)
        $text = [System.Windows.Media.FormattedText]::new(
            ([char]0x6587).ToString(),  # "文" — 品牌字标（曾误用 0x67A2 "枢"，已改回）
            [Globalization.CultureInfo]::GetCultureInfo('zh-CN'),
            [System.Windows.FlowDirection]::LeftToRight,
            $typeface,
            608.0 * $scale,
            $foregroundBrush,
            1.0)
        $origin = [System.Windows.Point]::new(
            ($Size - $text.WidthIncludingTrailingWhitespace) / 2.0,
            ($Size - $text.Height) / 2.0 - 24.0 * $scale)
        $context.DrawText($text, $origin)
    }
    finally {
        $context.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size,
        $Size,
        96.0,
        96.0,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Write-DocPivotIco {
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [Collections.Generic.List[byte[]]] $PngImages,
        [Parameter(Mandatory)]
        [int[]] $Sizes
    )

    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$Sizes.Count)
        $offset = 6 + 16 * $Sizes.Count
        for ($index = 0; $index -lt $Sizes.Count; $index++) {
            $sizeByte = if ($Sizes[$index] -eq 256) { 0 } else { $Sizes[$index] }
            $writer.Write([byte]$sizeByte)
            $writer.Write([byte]$sizeByte)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$PngImages[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $PngImages[$index].Length
        }

        foreach ($image in $PngImages) {
            $writer.Write($image)
        }

        [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Write-DocPivotIconSet {
    param(
        [Parameter(Mandatory)]
        [string] $Name,
        [Parameter(Mandatory)]
        [string] $Background,
        [Parameter(Mandatory)]
        [string] $Foreground
    )

    $pngPath = Join-Path $assets "app-icon-$Name.png"
    [System.IO.File]::WriteAllBytes(
        $pngPath,
        (New-DocPivotIconPng -Size 1024 -Background $Background -Foreground $Foreground))

    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $images = [Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $images.Add((New-DocPivotIconPng -Size $size -Background $Background -Foreground $Foreground))
    }
    Write-DocPivotIco -Path (Join-Path $assets "app-icon-$Name.ico") -PngImages $images -Sizes $sizes
}

Write-DocPivotIconSet -Name 'black' -Background '#151515' -Foreground '#D9B872'
Write-DocPivotIconSet -Name 'gold' -Background '#D9B872' -Foreground '#151515'
