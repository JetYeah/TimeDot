# Print RGB at given points of a PNG. Usage: -Path x.png -Points "77,77;80,80;118,118"
param([string]$Path, [string]$Points)
Add-Type -AssemblyName System.Drawing
$b = [System.Drawing.Bitmap]::FromFile($Path)
foreach ($s in $Points.Split(';')) {
    $xy = $s.Split(','); $x = [int]$xy[0]; $y = [int]$xy[1]
    $c = $b.GetPixel($x, $y)
    Write-Output ("({0},{1}) = {2},{3},{4}" -f $x, $y, $c.R, $c.G, $c.B)
}
$b.Dispose()
