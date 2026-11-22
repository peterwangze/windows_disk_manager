$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$exe = Join-Path ([IO.Path]::GetTempPath()) ("WdmUpdateTests-" + [Guid]::NewGuid().ToString('N') + ".exe")
try {
    $sources = @((Join-Path $PSScriptRoot '..\src\UpdateService.cs'), (Join-Path $PSScriptRoot 'UpdateServiceTests.cs'))
    & (Join-Path $framework 'csc.exe') /nologo /codepage:65001 /define:UPDATE_TESTS /target:exe "/out:$exe" "/r:$framework\System.Web.Extensions.dll" @sources
    if ($LASTEXITCODE) { throw 'Test build failed.' }
    & $exe
    if ($LASTEXITCODE) { throw 'Update service regression failed.' }
}
finally {
    if ([IO.File]::Exists($exe)) { [IO.File]::Delete($exe) }
}
