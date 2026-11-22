[CmdletBinding()]
param([string]$Executable)
$ErrorActionPreference = 'Stop'
if (!$Executable) { $Executable = Join-Path $PSScriptRoot '..\dist\WindowsDiskManager\WindowsDiskManager.exe' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$exe = (Resolve-Path -LiteralPath $Executable).Path
$tempDir = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$work = Join-Path $tempDir ('wdm-ui-smoke-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $work 'fixture'
$cache = Join-Path $work 'cache'
$null = New-Item -ItemType Directory -Path $fixture, $cache
$first = 'wdm-smoke-alpha.txt'
$second = 'wdm-smoke-beta.txt'
$keep = 'wdm-smoke-keep.txt'
[IO.File]::WriteAllText((Join-Path $fixture $first), 'first fixture file')
[IO.File]::WriteAllText((Join-Path $fixture $keep), 'retained control')
$desc = [System.Windows.Automation.TreeScope]::Descendants
$ae = [System.Windows.Automation.AutomationElement]
function Wait-For($description, [scriptblock]$read, [int]$timeout = 30) {
    $until = [DateTime]::UtcNow.AddSeconds($timeout)
    do {
        try { $value = & $read; if ($value) { return $value } }
        catch [System.Windows.Automation.ElementNotAvailableException] {}
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for $description."
}
function By-Id($parent, $id) {
    $c = [System.Windows.Automation.PropertyCondition]::new($ae::AutomationIdProperty, $id)
    return $parent.FindFirst($desc, $c)
}
function Row($name) {
    $type = [System.Windows.Automation.PropertyCondition]::new($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::TreeItem)
    $named = [System.Windows.Automation.PropertyCondition]::new($ae::NameProperty, $name)
    return $win.FindFirst($desc, [System.Windows.Automation.AndCondition]::new($type, $named))
}
function Invoke-Button($button) {
    if (!$button -or !$button.Current.IsEnabled) { throw 'Button is missing or disabled.' }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Confirm-Dialog {
    $control = Wait-For 'confirmation dialog' {
        $topLevel = $ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.PropertyCondition]::new($ae::ProcessIdProperty, $app.Id))
        foreach ($top in $topLevel) {
            $pathsBox = By-Id $top 'ConfirmPaths'
            if ($pathsBox) { return $pathsBox }
        }
    }
    $lines = $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -split '\r?\n'
    $expected = @((Join-Path $fixture $first), (Join-Path $fixture $second))
    if ($lines.Count -ne 2 -or @(Compare-Object $expected $lines).Count) { throw 'Unexpected confirmation paths.' }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    while ($control -and $control.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) {
        $control = $walker.GetParent($control)
    }
    if (!$control -or $control.Current.ProcessId -ne $app.Id) { throw 'Unexpected confirmation owner.' }
    return $control
}
function Start-TestApp {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $exe
    $start.WorkingDirectory = Split-Path -Parent $exe
    $start.UseShellExecute = $false
    $start.EnvironmentVariables['WDM_DATA_DIR'] = $cache
    $start.EnvironmentVariables['WDM_SCAN_ROOT'] = $fixture
    return [Diagnostics.Process]::Start($start)
}
$app = $null
$passed = $false
try {
    $app = Start-TestApp
    $win = Wait-For 'manager window' {
        $app.Refresh()
        if ($app.MainWindowHandle -ne 0) { $ae::FromHandle($app.MainWindowHandle) }
    }
    $null = Wait-For 'first-run scan' { Row $first }
    [IO.File]::WriteAllText((Join-Path $fixture $second), 'created after the first scan')
    Invoke-Button (By-Id $win 'Refresh')
    $null = Wait-For 'new file after refresh' { Row $second }
    foreach ($name in $first, $second) {
        $box = By-Id (Row $name) 'SelectItem'
        $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    }
    Invoke-Button (By-Id $win 'RecycleSelection')
    $dialog = Confirm-Dialog
    Invoke-Button (By-Id $dialog 'CancelRecycle')
    foreach ($name in $first, $second, $keep) {
        if (!(Test-Path -LiteralPath (Join-Path $fixture $name) -PathType Leaf)) { throw 'Cancel changed the fixture.' }
    }

    Invoke-Button (By-Id $win 'RecycleSelection')
    $dialog = Confirm-Dialog
    # Every checked row is a uniquely named file created above, in a fresh isolated scan root.
    Invoke-Button (By-Id $dialog 'ConfirmRecycle')
    $null = Wait-For 'both fixture files to reach the recycle bin' {
        !(Test-Path -LiteralPath (Join-Path $fixture $first)) -and !(Test-Path -LiteralPath (Join-Path $fixture $second))
    }
    $null = Wait-For 'updated tree after recycling' { (Row $keep) -and !(Row $first) -and !(Row $second) }
    if (!(Test-Path -LiteralPath (Join-Path $fixture $keep) -PathType Leaf)) { throw 'Unchecked file disappeared.' }
    $bin = (New-Object -ComObject Shell.Application).Namespace(10)
    $recycled = @($bin.Items() | Where-Object { $_.ExtendedProperty('System.Recycle.DeletedFrom') -eq $fixture })
    if ($recycled.Count -ne 2) { throw 'Could not verify both items in the Windows Recycle Bin.' }
    foreach ($entry in $recycled) { $entry.InvokeVerb('undelete') }
    $null = Wait-For 'fixture files to restore' {
        [IO.File]::Exists((Join-Path $fixture $first)) -and [IO.File]::Exists((Join-Path $fixture $second))
    }
    $passed = $true
    Write-Output 'PASS: first-run scan, manual refresh, cancel, batch recycle, retention of unchecked file, and updated tree.'
} finally {
    if ($app -and !$app.HasExited) {
        $null = $app.CloseMainWindow()
        if (!$app.WaitForExit(12000)) { Write-Warning "Test instance is still open (PID $($app.Id))." }
    }
    if ($passed -and (!$app -or $app.HasExited)) {
        $resolved = (Resolve-Path -LiteralPath $work).Path
        if ([IO.Path]::GetDirectoryName($resolved) -eq $tempDir -and [IO.Path]::GetFileName($resolved) -match '^wdm-ui-smoke-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    } else { Write-Output "Fixture retained at $work." }
}
