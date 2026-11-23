[CmdletBinding()]
param([string]$Executable)
$ErrorActionPreference = 'Stop'
if (!$Executable) { $Executable = Join-Path $PSScriptRoot '..\dist\WindowsDiskManager\WindowsDiskManager.exe' }
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$exe = (Resolve-Path -LiteralPath $Executable).Path
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$work = Join-Path $temp ('wdm-scan-feedback-' + [guid]::NewGuid().ToString('N'))
$cache = Join-Path $work 'cache'
$fixture = Join-Path $work 'fixture'
$copy = Join-Path $work 'app'
$null = New-Item -ItemType Directory -Path $cache,$fixture,$copy -Force
$app = $null
$success = $false
function Need($value, [string]$message) { if (!$value) { throw $message } }
function Wait-Until([string]$message, [scriptblock]$read) {
    $check = [Diagnostics.Stopwatch]::StartNew()
    do {
        try { $value = & $read; if ($value) { return $value } }
        catch [System.Windows.Automation.ElementNotAvailableException] {}
        Start-Sleep -Milliseconds 50
    } while ($check.Elapsed.TotalSeconds -lt 20)
    throw "Timed out: $message"
}
function By-Id($parent, [string]$id) {
    $parent.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Row([string]$name) {
    $byName = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)
    $byKind = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)
    $win.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.AndCondition]::new($byName,$byKind))
}
function Click($control) {
    Need ($null -ne $control -and $control.Current.IsEnabled) 'Expected an enabled action'
    $control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Seed([string]$output) {
    & (Join-Path (Split-Path -Parent $exe) 'DiskScanner.exe') --root $fixture --output $output | Out-Null
    Need ($LASTEXITCODE -eq 0 -and [IO.File]::Exists($output)) 'Real scanner did not create the test snapshot'
}

# Delay and release the scanner at deterministic points without delaying the application's
# dispatcher. Everything except this fixture helper is the packaged application's real code.
$helper = @'
using System;
using System.IO;
using System.Text;
using System.Threading;
class FeedbackScanner {
    static int Main(string[] args) {
        Console.OutputEncoding = new UTF8Encoding(false);
        var work = Environment.GetEnvironmentVariable("WDM_FEEDBACK_TEST");
        var begin = Path.Combine(work,"begin");
        var finish = Path.Combine(work,"finish");
        var cancel = args[Array.IndexOf(args,"--cancel-file")+1];
        var output = args[Array.IndexOf(args,"--output")+1];
        File.AppendAllText(Path.Combine(work,"calls"),"start\n");
        for (int tick=0;tick<1200;tick++) {
            if (File.Exists(cancel)) { Console.WriteLine("CANCELLED"); return 2; }
            if (File.Exists(begin)) {
                Console.WriteLine("SCAN\tC:\\\t12\t340");
                Console.WriteLine("SCAN\tD:\\\t56\t780");
                Console.WriteLine("STAGE\tfixture aggregation");
                while (!File.Exists(finish) && !File.Exists(cancel) && tick++<1200) Thread.Sleep(50);
                if (File.Exists(cancel)) { Console.WriteLine("CANCELLED"); return 2; }
                if (!File.Exists(finish)) return 1;
                if (File.ReadAllText(finish)=="fail") { Console.WriteLine("ERROR\tfixture intentional failure"); return 1; }
                File.Copy(Path.Combine(work,"next.sqlite"),output);
                Console.WriteLine("DONE\t"+output); return 0;
            }
            Thread.Sleep(50);
        }
        return 1;
    }
}
'@
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class FeedbackWindow {
    [DllImport("user32")] public static extern bool ShowWindowAsync(IntPtr h, int command);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr h,int msg,IntPtr w,IntPtr l,uint flags,uint ms,out IntPtr result);
    public static long Ping(IntPtr h) {
        var watch=Stopwatch.StartNew();IntPtr result;
        if(SendMessageTimeout(h,0,IntPtr.Zero,IntPtr.Zero,2,750,out result)==IntPtr.Zero) return 750;
        return watch.ElapsedMilliseconds;
    }
}
'@
try {
    Copy-Item -LiteralPath $exe -Destination $copy
    Copy-Item -LiteralPath ($exe+'.config') -Destination $copy
    $cs = Join-Path $work 'FeedbackScanner.cs'
    [IO.File]::WriteAllText($cs,$helper)
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /target:exe "/out:$copy\DiskScanner.exe" $cs
    Need ($LASTEXITCODE -eq 0) 'Fixture helper build failed'
    $old = 'previous-snapshot.txt'
    $new = 'new-snapshot.txt'
    [IO.File]::WriteAllText((Join-Path $fixture $old),'old')
    Seed (Join-Path $cache 'disk-usage.sqlite')
    [IO.File]::WriteAllText((Join-Path $fixture $new),'new')
    Seed (Join-Path $work 'next.sqlite')
    $psi = [Diagnostics.ProcessStartInfo]::new((Join-Path $copy 'WindowsDiskManager.exe'))
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['WDM_DATA_DIR']=$cache
    $psi.EnvironmentVariables['WDM_SCAN_ROOT']=$fixture
    $psi.EnvironmentVariables['WDM_FEEDBACK_TEST']=$work
    $app = [Diagnostics.Process]::Start($psi)
    $win = Wait-Until 'owned window' {
        $app.Refresh()
        if ($app.MainWindowHandle -ne [IntPtr]::Zero) {
            $candidate=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
            if (By-Id $candidate 'UsageTree') { $candidate }
        }
    }
    $null=[FeedbackWindow]::ShowWindowAsync($app.MainWindowHandle,9)
    $row = Wait-Until 'seed snapshot' { Row $old }
    (By-Id $row 'SelectItem').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    $refresh = By-Id $win 'Refresh'
    $begin = Join-Path $work 'begin'; $finish = Join-Path $work 'finish'
    $attempt = 0
    foreach ($exitMode in @('cancel','fail','success')) {
        $attempt++
        [IO.File]::Delete($begin); [IO.File]::Delete($finish)
        $clickTime = [Diagnostics.Stopwatch]::StartNew()
        Click $refresh
        $cancel = Wait-Until 'visible cancel action before any scanner output' { $c=By-Id $win 'CancelScan'; if ($c -and $c.Current.IsEnabled -and !$c.Current.IsOffscreen) { $c } }
        Need ($clickTime.Elapsed.TotalSeconds -lt 3) 'Feedback was delayed while scanner had no output'
        $stage = By-Id $win 'ScanStage'; $counts = By-Id $win 'ScanCounts'; $elapsed = By-Id $win 'ScanElapsed'; $progress = By-Id $win 'ScanProgress'
        foreach($item in @($stage,$counts,$elapsed,$progress)) {
            Need ($null -ne $item -and !$item.Current.IsOffscreen -and $item.Current.BoundingRectangle.Height -gt 0) 'Missing visible scan indicator'
        }
        foreach($id in @('Refresh','UsageTree','CheckUpdates','ClearSelection','RecycleSelection')) {
            $action=By-Id $win $id
            Need ($null -ne $action -and !$action.Current.IsEnabled) "$id was active while scanning"
        }
        if ([FeedbackWindow]::GetForegroundWindow() -eq $app.MainWindowHandle) { Need $cancel.Current.HasKeyboardFocus 'Cancel did not receive keyboard focus' }
        $null=[FeedbackWindow]::PostMessage($app.MainWindowHandle,0x100,[IntPtr]116,[IntPtr]1)
        $null=[FeedbackWindow]::PostMessage($app.MainWindowHandle,0x101,[IntPtr]116,[IntPtr]1)
        $startText = $elapsed.Current.Name
        $null=Wait-Until 'elapsed clock during completely silent helper' {
            $sample=[FeedbackWindow]::Ping($app.MainWindowHandle)
            if ($sample -ge 750) { throw 'Window message pump timed out during refresh' }
            $elapsed.Current.Name -ne $startText
        }
        [IO.File]::WriteAllText($begin,'progress')
        $null=Wait-Until 'aggregated progress from two volumes' { $counts.Current.Name -match '68\D' -and $counts.Current.Name -match '1[,. ]?120\D' }
        $null=Wait-Until 'stage changed, totals retained' { $stage.Current.Name -eq 'fixture aggregation' }
        Need ([IO.File]::ReadAllLines((Join-Path $work 'calls')).Length -eq $attempt) 'Repeated F5 launched duplicate scan'
        if ($exitMode -eq 'cancel') { Click $cancel } else { [IO.File]::WriteAllText($finish,$exitMode) }
        $null=Wait-Until 'controls restored' { $refresh.Current.IsEnabled -and (By-Id $win 'UsageTree').Current.IsEnabled }
        Need ((By-Id $win 'CheckUpdates').Current.IsEnabled) 'Update action remained disabled'
        $count=(By-Id $win 'SelectionCount').Current.Name
        if ($exitMode -eq 'success') {
            Need ((Row $new) -and $count -match '0') 'New snapshot not published or selection not reset'
        } else {
            Need ((Row $old) -and !(Row $new) -and $count -match '1' -and (By-Id $win 'RecycleSelection').Current.IsEnabled) 'Previous snapshot or selection was lost'
            if ($exitMode -eq 'fail') { Need ((By-Id $win 'ScanStatus').Current.Name -like '*fixture intentional failure*') 'Scanner failure hidden' }
        }
        Write-Output "PASS $exitMode - visible progress, message pump, disabled actions, snapshot and selection."
    }
    $success = $true
}
finally {
    if ($app -and !$app.HasExited) { $null=$app.CloseMainWindow(); $null=$app.WaitForExit(15000) }
    $full=[IO.Path]::GetFullPath($work)
    if ($success -and (!$app -or $app.HasExited) -and [IO.Path]::GetDirectoryName($full) -eq $temp -and [IO.Path]::GetFileName($full) -match '^wdm-scan-feedback-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $full -Recurse -Force
    } else { Write-Output "Test evidence: $work" }
}
