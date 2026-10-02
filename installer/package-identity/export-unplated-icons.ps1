# Export Windows taskbar resource variants from the existing transparent logo; no redesign.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$source = [Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'Assets\Square150x150Logo.png'))
try {
    foreach ($size in @(16, 24, 32, 48, 256)) {
        $bitmap = New-Object Drawing.Bitmap($size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb))
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($source, 0, 0, $size, $size)
            foreach ($variant in @('unplated', 'lightunplated')) {
                $bitmap.Save((Join-Path $PSScriptRoot "Assets\Square44x44Logo.targetsize-${size}_altform-${variant}.png"), [Drawing.Imaging.ImageFormat]::Png)
            }
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
