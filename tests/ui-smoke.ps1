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
$branch = 'wdm-smoke-expand'
$nested = 'wdm-smoke-nested'
$leaf = 'wdm-smoke-leaf.txt'
[IO.File]::WriteAllText((Join-Path $fixture $first), 'first fixture file')
[IO.File]::WriteAllText((Join-Path $fixture $keep), 'retained control')
$nestedPath = Join-Path (Join-Path $fixture $branch) $nested
$null = New-Item -ItemType Directory -Path $nestedPath -Force
[IO.File]::WriteAllText((Join-Path $nestedPath $leaf), 'expand this directory')
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
    $type = [System.Windows.Automation.PropertyCondition]::new($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $named = [System.Windows.Automation.PropertyCondition]::new($ae::NameProperty, $name)
    return $win.FindFirst($desc, [System.Windows.Automation.AndCondition]::new($type, $named))
}
function Invoke-Button($button) {
    if (!$button -or !$button.Current.IsEnabled) { throw 'Button is missing or disabled.' }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Toggle-Directory($row) {
    $toggle = By-Id $row 'ToggleDirectory'
    if (!$toggle -or !$toggle.Current.IsEnabled) { throw 'Directory toggle is missing or disabled.' }
    $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
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
        if ($app.MainWindowHandle -ne [IntPtr]::Zero) {
            $candidate = $ae::FromHandle($app.MainWindowHandle)
            if (By-Id $candidate 'UsageTree') { return $candidate }
        }
    }
    $null = Wait-For 'first-run scan' { (Row $first) -and (By-Id $win 'Refresh').Current.IsEnabled }
    $anchor = (Row $branch).Current.BoundingRectangle.Top
    Toggle-Directory (Row $branch)
    $null = Wait-For 'first nested directory' { Row $nested }
    Toggle-Directory (Row $nested)
    $null = Wait-For 'file under second nested directory' { Row $leaf }
    if ([Math]::Abs((Row $branch).Current.BoundingRectangle.Top - $anchor) -gt 1) { throw 'Expanded rows shifted the directory anchor.' }
    Toggle-Directory (Row $branch)
    $null = Wait-For 'collapsed descendants' { !(Row $nested) -and !(Row $leaf) }
    [IO.File]::WriteAllText((Join-Path $fixture $second), 'created after the first scan')
    $snapshot = Join-Path $cache 'disk-usage.sqlite'
    $previousWrite = [IO.File]::GetLastWriteTimeUtc($snapshot)
    $priorRuns = @([IO.Directory]::GetDirectories($cache, '.wdm-run-*'))
    # WPF queues InvokePattern's action on the dispatcher. Its return alone does not
    # prove the button handled input after publishing the very first snapshot.
    Invoke-Button (By-Id $win 'Refresh')
    $null = Wait-For 'the refresh button to dispatch a second scan after first-run publication' {
        [IO.File]::GetLastWriteTimeUtc($snapshot) -ne $previousWrite -or
            @([IO.Directory]::GetDirectories($cache, '.wdm-run-*') | Where-Object { $_ -notin $priorRuns }).Count -gt 0
    } 12
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
    Write-Output 'PASS: first-run scan, stable directory expansion and collapse, refresh, cancel, batch recycle, and updated tree.'
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
