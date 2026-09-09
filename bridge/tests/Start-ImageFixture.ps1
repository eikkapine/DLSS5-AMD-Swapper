param(
    [Parameter(Mandatory = $true)][string]$ImagePath,
    [Parameter(Mandatory = $true)][string]$InfoPath,
    [ValidateRange(5, 600)][int]$LifetimeSeconds = 90
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$resolvedImage = (Resolve-Path -LiteralPath $ImagePath).Path
$image = [System.Drawing.Image]::FromFile($resolvedImage)
$form = New-Object System.Windows.Forms.Form
$form.Text = 'NR Static Image Fixture'
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(16, 16)
$form.ClientSize = New-Object System.Drawing.Size($image.Width, $image.Height)
$form.ShowInTaskbar = $true

$picture = New-Object System.Windows.Forms.PictureBox
$picture.Dock = [System.Windows.Forms.DockStyle]::Fill
$picture.SizeMode = [System.Windows.Forms.PictureBoxSizeMode]::Normal
$picture.Image = $image
$picture.BackColor = [System.Drawing.Color]::Black
$form.Controls.Add($picture)

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = [Math]::Max(1000, $LifetimeSeconds * 1000)
$timer.Add_Tick({ $form.Close() })
$form.Add_Shown({
    [pscustomobject]@{
        pid = $PID
        hwnd = $form.Handle.ToInt64().ToString()
        width = $form.ClientSize.Width
        height = $form.ClientSize.Height
        image_sha256 = (Get-FileHash -LiteralPath $resolvedImage -Algorithm SHA256).Hash.ToLowerInvariant()
    } | ConvertTo-Json | Set-Content -LiteralPath $InfoPath -Encoding UTF8
    $timer.Start()
})

try {
    [System.Windows.Forms.Application]::Run($form)
}
finally {
    $timer.Dispose()
    $picture.Dispose()
    $image.Dispose()
    $form.Dispose()
}
