[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'src\SshWeave.Desktop.Windows\Assets'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.X, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-WeavePath {
    param(
        [int]$Size,
        [bool]$UpperFirst
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    if ($UpperFirst) {
        $path.AddBezier(
            [float]($Size * 0.18), [float]($Size * 0.31),
            [float]($Size * 0.38), [float]($Size * 0.28),
            [float]($Size * 0.35), [float]($Size * 0.70),
            [float]($Size * 0.55), [float]($Size * 0.69))
        $path.AddBezier(
            [float]($Size * 0.55), [float]($Size * 0.69),
            [float]($Size * 0.73), [float]($Size * 0.69),
            [float]($Size * 0.70), [float]($Size * 0.35),
            [float]($Size * 0.83), [float]($Size * 0.35))
    }
    else {
        $path.AddBezier(
            [float]($Size * 0.18), [float]($Size * 0.68),
            [float]($Size * 0.38), [float]($Size * 0.70),
            [float]($Size * 0.35), [float]($Size * 0.31),
            [float]($Size * 0.55), [float]($Size * 0.32))
        $path.AddBezier(
            [float]($Size * 0.55), [float]($Size * 0.32),
            [float]($Size * 0.73), [float]($Size * 0.32),
            [float]($Size * 0.70), [float]($Size * 0.66),
            [float]($Size * 0.83), [float]($Size * 0.66))
    }
    return $path
}

function New-LogoBitmap {
    param(
        [int]$Size,
        [switch]$ConnectionBadge
    )

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $inset = [float]($Size * 0.055)
        $bounds = [System.Drawing.RectangleF]::new($inset, $inset, $Size - 2 * $inset, $Size - 2 * $inset)
        $backgroundPath = New-RoundedRectanglePath $bounds ([float]($Size * 0.19))
        try {
            $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                $bounds,
                [System.Drawing.Color]::FromArgb(255, 18, 28, 35),
                [System.Drawing.Color]::FromArgb(255, 34, 55, 59),
                135.0)
            try {
                $graphics.FillPath($background, $backgroundPath)
            }
            finally {
                $background.Dispose()
            }
            $border = [System.Drawing.Pen]::new(
                [System.Drawing.Color]::FromArgb(180, 100, 132, 139),
                [float][Math]::Max(1.0, $Size * 0.018))
            try {
                $graphics.DrawPath($border, $backgroundPath)
            }
            finally {
                $border.Dispose()
            }
        }
        finally {
            $backgroundPath.Dispose()
        }

        $upper = New-WeavePath $Size $true
        $lower = New-WeavePath $Size $false
        try {
            $shadowWidth = [float][Math]::Max(2.0, $Size * 0.125)
            $strokeWidth = [float][Math]::Max(1.5, $Size * 0.076)
            $shadow = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(210, 7, 13, 17), $shadowWidth)
            $cyan = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 32, 196, 244), $strokeWidth)
            $green = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 66, 211, 146), $strokeWidth)
            foreach ($pen in @($shadow, $cyan, $green)) {
                $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            }
            try {
                $graphics.DrawPath($shadow, $upper)
                $graphics.DrawPath($shadow, $lower)
                $graphics.DrawPath($cyan, $upper)
                $graphics.DrawPath($green, $lower)
            }
            finally {
                $shadow.Dispose()
                $cyan.Dispose()
                $green.Dispose()
            }
        }
        finally {
            $upper.Dispose()
            $lower.Dispose()
        }

        $nodeRadius = [float][Math]::Max(1.4, $Size * 0.038)
        $nodeBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 245, 185, 66))
        try {
            foreach ($point in @(
                @([float]($Size * 0.18), [float]($Size * 0.31)),
                @([float]($Size * 0.18), [float]($Size * 0.68)),
                @([float]($Size * 0.83), [float]($Size * 0.35)),
                @([float]($Size * 0.83), [float]($Size * 0.66)))) {
                $graphics.FillEllipse(
                    $nodeBrush,
                    $point[0] - $nodeRadius,
                    $point[1] - $nodeRadius,
                    $nodeRadius * 2,
                    $nodeRadius * 2)
            }
        }
        finally {
            $nodeBrush.Dispose()
        }

        if ($ConnectionBadge) {
            $badgeRadius = [float]($Size * 0.155)
            $badgeX = [float]($Size * 0.76)
            $badgeY = [float]($Size * 0.76)
            $badgeBorder = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 18, 28, 35))
            $badge = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 245, 185, 66))
            $mark = [System.Drawing.Pen]::new(
                [System.Drawing.Color]::FromArgb(255, 18, 28, 35),
                [float][Math]::Max(1.3, $Size * 0.036))
            $mark.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $mark.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            try {
                $graphics.FillEllipse(
                    $badgeBorder,
                    $badgeX - $badgeRadius * 1.12,
                    $badgeY - $badgeRadius * 1.12,
                    $badgeRadius * 2.24,
                    $badgeRadius * 2.24)
                $graphics.FillEllipse(
                    $badge,
                    $badgeX - $badgeRadius,
                    $badgeY - $badgeRadius,
                    $badgeRadius * 2,
                    $badgeRadius * 2)
                $graphics.DrawLines($mark, [System.Drawing.PointF[]]@(
                    [System.Drawing.PointF]::new($badgeX - $Size * 0.035, $badgeY - $Size * 0.060),
                    [System.Drawing.PointF]::new($badgeX + $Size * 0.035, $badgeY),
                    [System.Drawing.PointF]::new($badgeX - $Size * 0.035, $badgeY + $Size * 0.060)))
            }
            finally {
                $badgeBorder.Dispose()
                $badge.Dispose()
                $mark.Dispose()
            }
        }
    }
    finally {
        $graphics.Dispose()
    }
    return $bitmap
}

function Convert-BitmapToPngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $stream = [System.IO.MemoryStream]::new()
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Write-MultiSizeIcon {
    param(
        [string]$Path,
        [switch]$ConnectionBadge
    )

    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $images = @()
    foreach ($size in $sizes) {
        $bitmap = New-LogoBitmap $size -ConnectionBadge:$ConnectionBadge
        try {
            $images += ,(Convert-BitmapToPngBytes $bitmap)
        }
        finally {
            $bitmap.Dispose()
        }
    }

    $stream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $size = $sizes[$index]
            $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
            $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$index].Length
        }
        foreach ($image in $images) {
            $writer.Write($image)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$logoPath = Join-Path $OutputDirectory 'SshWeave-logo.png'
$logo = New-LogoBitmap 1024
try {
    $logo.Save($logoPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $logo.Dispose()
}

Write-MultiSizeIcon (Join-Path $OutputDirectory 'SshWeave.ico')
Write-MultiSizeIcon (Join-Path $OutputDirectory 'SshWeave.Connection.ico') -ConnectionBadge

Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name | ForEach-Object {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName
    Write-Output "$($_.Name)`t$($_.Length)`t$($hash.Hash)"
}
