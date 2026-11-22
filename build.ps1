[CmdletBinding()]
param(
    [ValidateSet('All', 'Viewer', 'Scanner', 'Package')] [string]$Part = 'All',
    [string]$Python = 'python',
    [string]$InnoCompiler
)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'src'
$build = Join-Path $PSScriptRoot 'build'
$dist = Join-Path $PSScriptRoot 'dist'
$product = Join-Path $dist 'WindowsDiskManager'
$null = New-Item -ItemType Directory -Path $build, $product -Force

if ($Part -in 'All','Viewer') {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
    if (!(Test-Path -LiteralPath $compiler)) { throw 'The Microsoft .NET Framework x64 compiler is required.' }
    $arguments = @('/nologo', '/noconfig', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
        '/main:UsageTree.Program', "/out:$product\WindowsDiskManager.exe", "/win32manifest:$src\app.manifest",
        "/win32icon:$src\app.ico", "/resource:$src\Viewer.xaml,Viewer.xaml")
    foreach ($name in 'System', 'System.Core', 'System.Xaml', 'System.Xml', 'System.Web.Extensions') {
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

if ($Part -in 'All','Scanner') {
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

if ($Part -in 'All','Package') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination $product -Force
    $assembly = Get-Content -LiteralPath (Join-Path $src 'AssemblyInfo.cs') -Raw
    if ($assembly -notmatch 'AssemblyInformationalVersion\("(\d+\.\d+\.\d+)"\)') { throw 'Invalid application version.' }
    $version = $Matches[1]
    $viewerPath = Join-Path $product 'WindowsDiskManager.exe'
    $pe = (Get-Item -LiteralPath $viewerPath).VersionInfo
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($viewerPath).Version.ToString()
    if ($pe.ProductVersion -ne $version -or $pe.FileVersion -ne "$version.0" -or $assemblyVersion -ne "$version.0") {
        throw 'The compiled ProductVersion, FileVersion and assembly version must match AssemblyInfo.cs. Rebuild before packaging.'
    }
    $zip = Join-Path $dist "WindowsDiskManager-$version-win-x64.zip"
    $payload = @('WindowsDiskManager.exe','WindowsDiskManager.exe.config','DiskScanner.exe','Install.ps1','licenses') |
        ForEach-Object { Join-Path $product $_ }
    Compress-Archive -LiteralPath $payload -DestinationPath $zip -CompressionLevel Optimal -Force
    $pathCompiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $candidates = @($InnoCompiler, (Join-Path $build 'tools\InnoSetup-6.7.4\ISCC.exe'), $pathCompiler.Source,
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))
    $compiler = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if (!$compiler) { throw 'Install Inno Setup 6.5+ or supply -InnoCompiler <path-to-ISCC.exe>.' }
    & $compiler '/Qp' "/DAppVersion=$version" "/DProductDir=$product" "/DDistDir=$dist" "$PSScriptRoot\installer\WindowsDiskManager.iss"
    if ($LASTEXITCODE -ne 0) { throw 'Single-file installer compilation failed.' }
    $setup = Join-Path $dist "WindowsDiskManager-$version-win-x64-Setup.exe"
    Get-Item -LiteralPath $zip, $setup | Select-Object FullName, Length
}
