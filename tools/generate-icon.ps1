$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\CodexAutoContinue.App\Assets'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$bitmap = New-Object System.Drawing.Bitmap 64,64
$g = [System.Drawing.Graphics]::FromImage($bitmap)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::FromArgb(16,19,28))
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(128,225,193)),6
$g.DrawArc($pen,11,11,42,42,42,280)
$points = [System.Drawing.Point[]]@([System.Drawing.Point]::new(49,10),[System.Drawing.Point]::new(55,26),[System.Drawing.Point]::new(38,23))
$brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(128,225,193))
$g.FillPolygon($brush,$points)
$g.FillPolygon($brush,[System.Drawing.Point[]]@([System.Drawing.Point]::new(27,22),[System.Drawing.Point]::new(27,43),[System.Drawing.Point]::new(43,32)))
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$stream = [System.IO.File]::Create((Join-Path $out 'app.ico'))
$icon.Save($stream)
$stream.Dispose(); $icon.Dispose(); $g.Dispose(); $pen.Dispose(); $brush.Dispose(); $bitmap.Dispose()
