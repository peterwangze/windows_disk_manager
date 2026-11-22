using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace UsageTree
{
    internal static class InstanceLock
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int show);

        internal static FileStream Acquire(string directory)
        {
            string path = Path.Combine(directory, "manager.lock");
            FileStream stream;
            try { stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
            catch (IOException ex)
            {
                if ((ex.HResult & 65535) != 32 && (ex.HResult & 65535) != 33) throw;
                try
                {
                    using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                    using (var process = Process.GetProcessById(Int32.Parse(reader.ReadToEnd(), CultureInfo.InvariantCulture)))
                    {
                        var hwnd = process.MainWindowHandle;
                        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9);
                        SetForegroundWindow(hwnd);
                    }
                }
                catch (IOException) { }
                catch (ArgumentException) { }
                catch (FormatException) { }
                catch (InvalidOperationException) { }
                return null;
            }
            try
            {
                byte[] id = Encoding.ASCII.GetBytes(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                stream.SetLength(0);
                stream.Write(id, 0, id.Length);
                stream.Flush();
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
    }

    // Keep the helper and its descendants tied to the application's lifetime on Windows.
    internal sealed class ScanJob : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Limits
        {
            public long PerProcessTime, PerJobTime;
            public uint Flags;
            public UIntPtr MinWorkset, MaxWorkset;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public Limits Basic;
            public ulong Reads, Writes, Other, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob;
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, int size);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        private SafeFileHandle job;
        internal void Attach(Process process)
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) return;
            var limits = new ExtendedLimits();
            limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf(typeof(ExtendedLimits))) ||
                !AssignProcessToJobObject(job, process.Handle))
                Dispose(); // Nested jobs may be prohibited by the launching environment.
        }
        public void Dispose() { if (job != null) { job.Dispose(); job = null; } }
    }

    internal sealed class ScanRun : IDisposable
    {
        internal readonly string Output;
        private readonly string cancelPath, dataDirectory, stageDirectory;
        private readonly Process process = new Process();
        private readonly ScanJob job = new ScanJob();
        private readonly object gate = new object();
        private string lastError = "";
        private Timer watchdog;
        private bool started;
        internal bool Cancelled { get; private set; }

        internal ScanRun(string directory)
        {
            dataDirectory = directory;
            stageDirectory = ScanStaging.Create(directory);
            Output = Path.Combine(stageDirectory, "snapshot.sqlite");
            cancelPath = Path.Combine(stageDirectory, "cancel");
        }

        // All arguments are filesystem paths. Windows doubles trailing backslashes before a closing quote.
        private static string QuotePath(string path)
        {
            int trailing = path.Length - path.TrimEnd('\\').Length;
            return "\"" + path + new string('\\', trailing) + "\"";
        }

        internal void Start(Action<string> progress, Action<int,string> complete)
        {
            string helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DiskScanner.exe");
            if (!File.Exists(helper)) throw new FileNotFoundException("请将 DiskScanner.exe 和管理器放在同一目录。");
            string arguments = "--output " + QuotePath(Output) + " --exclude " + QuotePath(dataDirectory) + " --cancel-file " + QuotePath(cancelPath);
            string root = Environment.GetEnvironmentVariable("WDM_SCAN_ROOT");
            if (!String.IsNullOrWhiteSpace(root)) arguments += " --root " + QuotePath(Path.GetFullPath(root));
            process.StartInfo = new ProcessStartInfo(helper, arguments) {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                if (e.Data == null) return;
                if (e.Data.StartsWith("ERROR\t", StringComparison.Ordinal)) { lock (gate) lastError = e.Data.Substring(6); }
                else progress(e.Data);
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                if (!String.IsNullOrWhiteSpace(e.Data)) lock (gate) lastError = e.Data;
            };
            if (!process.Start()) throw new IOException("扫描器未能启动。");
            started = true;
            job.Attach(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            ThreadPool.QueueUserWorkItem(delegate {
                int code = 1;
                string detail;
                try { process.WaitForExit(); code = process.ExitCode; }
                catch (Exception ex) { lock (gate) lastError = ex.Message; }
                lock (gate) detail = lastError;
                complete(code, detail);
            });
        }

        internal void Cancel()
        {
            if (Cancelled) return;
            Cancelled = true;
            try { File.WriteAllText(cancelPath, ""); }
            catch (IOException) { StopImmediately(); return; }
            catch (UnauthorizedAccessException) { StopImmediately(); return; }
            watchdog = new Timer(delegate { StopImmediately(); }, null, 30000, Timeout.Infinite);
        }

        private void StopImmediately()
        {
            lock (gate)
            {
                job.Dispose();
                try { if (started && !process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        }

        public void Dispose()
        {
            if (watchdog != null) watchdog.Dispose();
            StopImmediately();
            lock (gate) { started = false; process.Dispose(); }
            ScanStaging.Clean(stageDirectory);
        }
    }
}
