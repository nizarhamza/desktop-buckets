# Emits the MSIX logo PNGs (Square44x44Logo, Square150x150Logo, StoreLogo)
# into packaging/Images/. Same 2x2-tile mark as the app icon.
param([string]$OutDir = "$PSScriptRoot\Images")

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Save-Logo([int]$S, [string]$Path) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [int][Math]::Max(1, $S * 0.10)
    $rw = $S - 2 * $pad
    $r = [int][Math]::Max(2, $S * 0.20)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($pad, $pad, $r, $r, 180, 90)
    $p.AddArc($pad + $rw - $r, $pad, $r, $r, 270, 90)
    $p.AddArc($pad + $rw - $r, $pad + $rw - $r, $r, $r, 0, 90)
    $p.AddArc($pad, $pad + $rw - $r, $r, $r, 90, 90)
    $p.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(238, 26, 26, 30))
    $g.FillPath($bg, $p); $bg.Dispose()

    $gap = $rw * 0.11
    $cell = ($rw - $gap * 3) / 2
    $cols = @(
        [System.Drawing.Color]::FromArgb(255, 88, 166, 255),
        [System.Drawing.Color]::FromArgb(255, 255, 179, 41),
        [System.Drawing.Color]::FromArgb(255, 121, 224, 138),
        [System.Drawing.Color]::FromArgb(255, 240, 108, 122)
    )
    for ($i = 0; $i -lt 4; $i++) {
        $x = $pad + $gap + ($i % 2) * ($cell + $gap)
        $y = $pad + $gap + [int][Math]::Floor($i / 2) * ($cell + $gap)
        $tr = [Math]::Max(1.0, $cell * 0.24)
        $tp = New-Object System.Drawing.Drawing2D.GraphicsPath
        $tp.AddArc($x, $y, $tr, $tr, 180, 90)
        $tp.AddArc($x + $cell - $tr, $y, $tr, $tr, 270, 90)
        $tp.AddArc($x + $cell - $tr, $y + $cell - $tr, $tr, $tr, 0, 90)
        $tp.AddArc($x, $y + $cell - $tr, $tr, $tr, 90, 90)
        $tp.CloseFigure()
        $b = New-Object System.Drawing.SolidBrush($cols[$i])
        $g.FillPath($b, $tp); $b.Dispose(); $tp.Dispose()
    }
    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "  $Path"
}

Save-Logo 44  (Join-Path $OutDir 'Square44x44Logo.png')
Save-Logo 150 (Join-Path $OutDir 'Square150x150Logo.png')
Save-Logo 50  (Join-Path $OutDir 'StoreLogo.png')
