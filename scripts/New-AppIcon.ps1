# Rebuild the code-drawn robot tile; no downloaded fonts or artwork are used.
param([string]$OutputPath = (Join-Path $PSScriptRoot '..\src\WsaHelper.Desktop\Assets\WsaHelper.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$images = @()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $visual = New-Object Windows.Media.DrawingVisual
    $drawing = $visual.RenderOpen()
    try {
        $drawing.PushTransform((New-Object Windows.Media.ScaleTransform ($size/40.0),($size/40.0)))
        $green = [Windows.Media.BrushConverter]::new().ConvertFromString('#176747')
        $drawing.DrawRoundedRectangle($green,$null,[Windows.Rect]::new(0,0,40,40),11,11)
        $shape = [Windows.Media.Geometry]::Parse('M 10,20 A 10,10 0 0 1 30,20 L 30,28 Q 30,30 28,30 L 12,30 Q 10,30 10,28 Z M 12,9 L 16,15 M 28,9 L 24,15')
        $drawing.DrawGeometry([Windows.Media.Brushes]::White,[Windows.Media.Pen]::new([Windows.Media.Brushes]::White,2),$shape)
        foreach ($eye in @(16.25,24.25)) { $drawing.DrawEllipse($green,$null,[Windows.Point]::new($eye,20.25),1.25,1.25) }
        $drawing.Pop()
    } finally { $drawing.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = New-Object IO.MemoryStream
    try { $encoder.Save($memory); $images += [pscustomobject]@{ Size=$size; Bytes=$memory.ToArray() } } finally { $memory.Dispose() }
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
$writer = [IO.BinaryWriter]::new([IO.File]::Create($OutputPath))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
    $offset = 6 + 16*$images.Count
    foreach ($entry in $images) {
        $dimension = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$entry.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $images) { $writer.Write([byte[]]$entry.Bytes) }
} finally { $writer.Dispose() }
