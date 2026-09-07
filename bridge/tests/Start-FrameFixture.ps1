param(
    [int]$Width = 1280,
    [int]$Height = 720,
    [int]$LifetimeSeconds = 1800,
    [switch]$Animate,
    [Parameter(Mandatory)][string]$ReadyFile
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$fixtureForm = New-Object System.Windows.Forms.Form
$fixtureForm.Text = 'NR Performance Fixture'
$fixtureForm.FormBorderStyle = 'None'
$fixtureForm.StartPosition = 'Manual'
$fixtureForm.Location = New-Object System.Drawing.Point(8, 8)
$fixtureForm.ClientSize = New-Object System.Drawing.Size($Width, $Height)
$fixtureImage = New-Object System.Drawing.Bitmap($Width, $Height)
$fixtureGraphics = [System.Drawing.Graphics]::FromImage($fixtureImage)
$fixtureGraphics.Clear([System.Drawing.Color]::FromArgb(32, 36, 42))
for ($y = 0; $y -lt $Height; $y += 24) {
    for ($x = 0; $x -lt $Width; $x += 24) {
        $factor = if (([int]($x / 24) + [int]($y / 24)) % 2) { 0.55 } else { 1.0 }
        $fixtureBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb([int](255 * $x / $Width * $factor), [int](255 * $y / $Height * $factor), [int](160 * $factor)))
        $fixtureGraphics.FillRectangle($fixtureBrush, $x, $y, 24, 24)
        $fixtureBrush.Dispose()
    }
}
$fixturePen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 1)
for ($n = 0; $n -lt 20; $n++) {
    $fixtureGraphics.DrawEllipse($fixturePen, [int]($Width / 3) + $n * 3, [int]($Height / 4) + $n * 3, [int]($Width / 3) - $n * 6, [int]($Height / 2) - $n * 6)
}
$fixturePen.Dispose()
$fixtureGraphics.Dispose()
$fixtureForm.BackgroundImage = $fixtureImage
$fixtureForm.BackgroundImageLayout = 'None'
$animationTimer = New-Object System.Windows.Forms.Timer
$animationTimer.Interval = 16
$script:fixtureTick = 0
if ($Animate) {
    $fixtureForm.Add_Paint({
        param($sender, $paintEvent)
        $markerX = ($script:fixtureTick * 11) % [Math]::Max(1, ($Width - 96))
        $paintEvent.Graphics.FillRectangle([System.Drawing.Brushes]::White, $markerX, 24, 96, 48)
        $paintEvent.Graphics.FillRectangle([System.Drawing.Brushes]::Black, $markerX + 8, 32, 80, 32)
    })
    $animationTimer.Add_Tick({ $script:fixtureTick++; $fixtureForm.Invalidate() })
}
$fixtureTimer = New-Object System.Windows.Forms.Timer
$fixtureTimer.Interval = [Math]::Max(1000, $LifetimeSeconds * 1000)
$fixtureTimer.Add_Tick({ $fixtureForm.Close() })
$fixtureForm.Add_Shown({
    @{ pid = $PID; hwnd = $fixtureForm.Handle.ToInt64().ToString(); width = $fixtureForm.ClientSize.Width; height = $fixtureForm.ClientSize.Height } |
        ConvertTo-Json | Set-Content -LiteralPath $ReadyFile
    $fixtureTimer.Start()
    if ($Animate) { $animationTimer.Start() }
})
try { [System.Windows.Forms.Application]::Run($fixtureForm) }
finally { $animationTimer.Dispose(); $fixtureTimer.Dispose(); $fixtureImage.Dispose(); $fixtureForm.Dispose() }
