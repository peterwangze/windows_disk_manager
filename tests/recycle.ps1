# Run on Windows using powershell.exe -NoProfile -STA -File tests\recycle.ps1.
# This integration test recycles only items under its generated temporary directory.
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '..\src\Recycler.cs')
Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class RecycleFixtureIdentity {
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32", SetLastError=true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, [Out] byte[] buffer, int size);
    public static byte[] Read(string path) {
        using (var file=CreateFileW(path, 128, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero)) {
            if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] info=new byte[24];
            if (!GetFileInformationByHandleEx(file,18,info,24)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return info;
        }
    }
}
"@
function New-Target([string]$Path, [bool]$Directory = $false) {
    $metadata=[RecycleFixtureIdentity]::Read($Path)
    $target=New-Object UsageTree.RecycleTarget
    $target.Path=$Path
    $target.IsDirectory=$Directory
    $target.FileId=[byte[]]$metadata[8..23]
    $target.VolumeSerial=[BitConverter]::ToInt64($metadata,0)
    return $target
}
function Invoke-Targets($Items) {
    $targets=New-Object 'System.Collections.Generic.List[UsageTree.RecycleTarget]'
    foreach ($item in $Items) { $targets.Add($item) }
    [UsageTree.Recycler]::Recycle($targets,[IntPtr]::Zero)
}
function Assert-True($Value,[string]$Message) { if (!$Value) { throw $Message } }

$token='Wdm-RecycleTest-'+[Guid]::NewGuid().ToString('N')
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture=[IO.Path]::Combine($tempRoot,$token)
$folder=Join-Path $fixture 'folder'
$file=Join-Path $fixture 'only-file.txt'
$inside=Join-Path $folder 'inside.txt'
$changed=Join-Path $fixture 'changed.txt'
$redirect=Join-Path $fixture 'junction'
$keep=Join-Path $fixture 'keep'
$previousData=$env:WDM_DATA_DIR
try {
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    [IO.Directory]::CreateDirectory($keep) | Out-Null
    $env:WDM_DATA_DIR=$keep
    [IO.File]::WriteAllText($file,$token)
    [IO.File]::WriteAllText($inside,$token)
    [IO.File]::WriteAllText($changed,$token)
    $fileTarget=New-Target $file
    $insideTarget=New-Target $inside
    $folderTarget=New-Target $folder $true
    $changedTarget=New-Target $changed
    [IO.File]::Move($changed,(Join-Path $fixture 'original.txt'))
    [IO.File]::WriteAllText($changed,'replacement')
    $rootTarget=New-Target ([IO.Path]::GetPathRoot($fixture)) $true
    $keepTarget=New-Target $keep $true
    $ancestorTarget=New-Target $fixture $true
    $wrongType=New-Target $changed $true
    $wrongVolume=New-Target $changed
    $wrongVolume.VolumeSerial=$wrongVolume.VolumeSerial -bxor 1
    New-Item -ItemType Junction -Path $redirect -Target $folder | Out-Null
    $redirectedTarget=New-Target (Join-Path $redirect 'inside.txt')
    $cancelled=@(Invoke-Targets @())
    Assert-True (($cancelled.Count -eq 0) -and [IO.File]::Exists($file)) 'Empty selection must do nothing.'
    $blocked=@(Invoke-Targets @($rootTarget,$changedTarget,$keepTarget,$ancestorTarget,$wrongType,$wrongVolume,$redirectedTarget))
    Assert-True (($blocked.Count -eq 7) -and !($blocked | Where-Object Success)) ('An ineligible selection was accepted: '+($blocked.Message -join ' | '))
    Assert-True ([IO.File]::Exists($inside) -and [IO.File]::Exists($changed)) 'Replacement or junction referent changed.'
    [IO.Directory]::Delete($redirect)
    $mixed=@(Invoke-Targets @($insideTarget,$changedTarget,$fileTarget,$folderTarget))
    Assert-True (($mixed.Count -eq 4) -and $mixed[0].Success -and !$mixed[1].Success -and $mixed[2].Success -and $mixed[3].Success) ('Partial batch, folder and nested item: '+($mixed.Message -join ' | '))
    Assert-True (![IO.Directory]::Exists($folder) -and ![IO.File]::Exists($file) -and [IO.File]::Exists($changed)) 'Original location has incorrect state.'
    $bin=(New-Object -ComObject Shell.Application).Namespace(10)
    # Match the unique original location, then request restoration of our two entries only.
    $found=@($bin.Items() | Where-Object { $_.ExtendedProperty('System.Recycle.DeletedFrom') -eq $fixture })
    Assert-True ($found.Count -eq 2) ('Windows did not expose both fixture items in the Recycle Bin. Count '+$found.Count)
    foreach ($entry in $found) { $entry.InvokeVerb('undelete') }
    for($i=0; $i -lt 40 -and !([IO.File]::Exists($inside) -and [IO.File]::Exists($file));$i++){Start-Sleep -Milliseconds 100}
    Assert-True ([IO.File]::ReadAllText($file) -eq $token -and [IO.File]::ReadAllText($inside) -eq $token) 'Recycle Bin restored different data.'
    Write-Output 'PASS: native file + directory recycle and restore, mixed failures, ancestor overlap, empty/cancelled selection, volume, identity, type, root, protected data, junction ancestor.'
}
finally {
    $env:WDM_DATA_DIR=$previousData
    $verified=[IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($verified).TrimEnd('\') -eq $tempRoot.TrimEnd('\') -and [IO.Path]::GetFileName($verified) -eq $token) {
        if ([IO.Directory]::Exists($redirect)) { [IO.Directory]::Delete($redirect) }
        if ([IO.Directory]::Exists($verified)) { [IO.Directory]::Delete($verified,$true) }
    }
}
