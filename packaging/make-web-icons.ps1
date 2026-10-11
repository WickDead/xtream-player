# Deterministic rasterization of the app's geometric play mark; no external assets.
Add-Type -AssemblyName System.Drawing
$target = Join-Path $PSScriptRoot '../src/XtreamPlayer.Web/wwwroot'
foreach ($entry in @(@{ Name='apple-touch-icon.png'; Size=180 }, @{ Name='icon-192.png'; Size=192 }, @{ Name='icon-512.png'; Size=512 }, @{ Name='icon-maskable.png'; Size=512 })) {
    $n = $entry.Size
    $bitmap = [System.Drawing.Bitmap]::new($n, $n)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml('#101917'))
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#d0edab'))
    $g.FillEllipse($brush, [single]($n*.18), [single]($n*.18), [single]($n*.64), [single]($n*.64))
    $play = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#203019'))
    $points = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($n*.43,$n*.34),[System.Drawing.PointF]::new($n*.43,$n*.66),[System.Drawing.PointF]::new($n*.68,$n*.5))
    $g.FillPolygon($play,$points)
    $bitmap.Save((Join-Path $target $entry.Name),[System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $brush.Dispose(); $play.Dispose(); $bitmap.Dispose()
}
