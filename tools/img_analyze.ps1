# 分析截图:把与背景色接近/偏离的像素按灰度分级打印成 ASCII,定位残影几何
param([string]$Path, [int]$Step = 1)
Add-Type -AssemblyName System.Drawing
$b = [System.Drawing.Bitmap]::FromFile($Path)
Write-Output ("size={0}x{1}" -f $b.Width, $b.Height)
$bg = $b.GetPixel(2, 2)
Write-Output ("bg={0},{1},{2}" -f $bg.R, $bg.G, $bg.B)
for ($y = 0; $y -lt $b.Height; $y += $Step) {
    $line = ''
    for ($x = 0; $x -lt $b.Width; $x += $Step) {
        $c = $b.GetPixel($x, $y)
        $d = [math]::Abs($c.R - $bg.R) + [math]::Abs($c.G - $bg.G) + [math]::Abs($c.B - $bg.B)
        if ($d -lt 4) { $line += '.' }
        elseif ($d -lt 12) { $line += ':' }
        elseif ($d -lt 30) { $line += 'o' }
        elseif ($d -lt 120) { $line += 'O' }
        elseif ($c.R -gt 200 -and $c.G -gt 200) { $line += '#' }
        else { $line += '+' }
    }
    Write-Output ('{0,3} {1}' -f $y, $line)
}
$b.Dispose()
