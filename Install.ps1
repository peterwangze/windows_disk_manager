[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$payload = $PSScriptRoot
if (!(Test-Path -LiteralPath (Join-Path $payload 'WindowsDiskManager.exe') -PathType Leaf)) {
    $payload = Join-Path $PSScriptRoot 'dist\WindowsDiskManager'
}
$files = @('WindowsDiskManager.exe', 'WindowsDiskManager.exe.config', 'DiskScanner.exe', 'licenses\Python.txt', 'licenses\PyInstaller.txt')
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) { throw "Missing release file: $file" }
}
$version = (Get-Item -LiteralPath (Join-Path $payload 'WindowsDiskManager.exe')).VersionInfo.ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Unexpected application version.' }
$productRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\WindowsDiskManager'
$installDir = Join-Path $productRoot $version
$null = New-Item -ItemType Directory -Path (Join-Path $installDir 'licenses') -Force
foreach ($file in $files) {
    $source = Join-Path $payload $file
    $target = Join-Path $installDir $file
    $hash = (Get-FileHash -LiteralPath $source).Hash
    if ((Test-Path -LiteralPath $target) -and ((Get-FileHash -LiteralPath $target).Hash -eq $hash)) { continue }
    Copy-Item -LiteralPath $source -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target).Hash -ne $hash) { throw "Copy verification failed: $file" }
}

$executable = Join-Path $installDir 'WindowsDiskManager.exe'
$menu = [Environment]::GetFolderPath('Programs')
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $menu '磁盘空间管理器.lnk'))
$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation = "$executable,0"
$shortcut.Description = 'Windows Disk Manager'
$shortcut.Save()
Write-Output $executable
