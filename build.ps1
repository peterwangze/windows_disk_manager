[CmdletBinding()]
param(
    [ValidateSet('All', 'Viewer', 'Scanner')] [string]$Part = 'All',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'src'
$build = Join-Path $PSScriptRoot 'build'
$dist = Join-Path $PSScriptRoot 'dist'
$product = Join-Path $dist 'WindowsDiskManager'
$null = New-Item -ItemType Directory -Path $build, $product -Force

if ($Part -ne 'Scanner') {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
    if (!(Test-Path -LiteralPath $compiler)) { throw 'The Microsoft .NET Framework x64 compiler is required.' }
    $arguments = @('/nologo', '/noconfig', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
        '/main:UsageTree.Program', "/out:$product\WindowsDiskManager.exe", "/win32manifest:$src\app.manifest",
        "/win32icon:$src\app.ico", "/resource:$src\Viewer.xaml,Viewer.xaml")
    foreach ($name in 'System', 'System.Core', 'System.Xaml', 'System.Xml') {
        $arguments += "/reference:$framework\$name.dll"
    }
    foreach ($name in 'PresentationFramework', 'PresentationCore', 'WindowsBase', 'UIAutomationTypes', 'UIAutomationProvider') {
        $arguments += "/reference:$framework\WPF\$name.dll"
    }
    $arguments += @(Get-ChildItem -LiteralPath $src -Filter '*.cs' | ForEach-Object FullName)
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Viewer compilation failed.' }
    Copy-Item -LiteralPath "$src\WindowsDiskManager.exe.config" -Destination $product -Force
}

if ($Part -ne 'Viewer') {
    & $Python -c "import struct, sys; sys.exit(0 if struct.calcsize('P') == 8 else 'Use 64-bit Python to build the scanner.')"
    if ($LASTEXITCODE -ne 0) { throw 'A 64-bit Python installation is required.' }
    & $Python -m PyInstaller --noconfirm --noupx --onefile --console --log-level WARN --name DiskScanner --distpath $product --workpath "$build\scanner" --specpath $build "$src\scan.py"
    if ($LASTEXITCODE -ne 0) { throw 'Scanner compilation failed. Install PyInstaller in the selected Python environment.' }
    $licenses = Join-Path $product 'licenses'
    $null = New-Item -ItemType Directory -Path $licenses -Force
    $pythonLicense = & $Python -c "import pathlib,sys; print(pathlib.Path(sys.base_prefix, 'LICENSE.txt'))"
    $bootloaderLicense = & $Python -c "import importlib.metadata as m; d=m.distribution('pyinstaller'); print(d.locate_file(next(f for f in d.files if f.name=='COPYING.txt')))"
    Copy-Item -LiteralPath $pythonLicense -Destination (Join-Path $licenses 'Python.txt') -Force
    Copy-Item -LiteralPath $bootloaderLicense -Destination (Join-Path $licenses 'PyInstaller.txt') -Force
}

if ($Part -eq 'All') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination $product -Force
    $version = (Get-Item -LiteralPath "$product\WindowsDiskManager.exe").VersionInfo.ProductVersion
    $zip = Join-Path $dist "WindowsDiskManager-$version-win-x64.zip"
    $payload = @('WindowsDiskManager.exe','WindowsDiskManager.exe.config','DiskScanner.exe','Install.ps1','licenses') |
        ForEach-Object { Join-Path $product $_ }
    Compress-Archive -LiteralPath $payload -DestinationPath $zip -CompressionLevel Optimal -Force
    Get-Item -LiteralPath $zip | Select-Object FullName, Length
}
