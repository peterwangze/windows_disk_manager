using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        internal string Output { get; private set; }
        private readonly string dataDirectory;
        private readonly string previousSnapshot;
        private readonly object gate = new object();
        private string stageDirectory;
        private int startRequested;
        private volatile bool cancelled, disposed, finished;
        internal bool Cancelled { get { return cancelled; } }

        internal ScanRun(string directory, string previousSnapshot)
        {
            dataDirectory = directory;
            this.previousSnapshot = previousSnapshot;
        }

        // All arguments are filesystem paths. Windows doubles trailing backslashes before a closing quote.
        private static string QuotePath(string path)
        {
            int trailing = path.Length - path.TrimEnd('\\').Length;
            return "\"" + path + new string('\\', trailing) + "\"";
        }

        internal void Start(Action<string> progress, Action<int,string> complete)
        {
            if (disposed) throw new ObjectDisposedException("ScanRun");
            if (Interlocked.Exchange(ref startRequested, 1) != 0)
                throw new InvalidOperationException("扫描已经启动。");
            // A single background worker owns the process, its pipes, job handle and
            // staging I/O. No start, wait, cancellation-file write or cleanup blocks WPF.
            var worker = new Thread(delegate() {
                int code = 2;
                string detail = "";
                try
                {
                    if (!cancelled && !disposed)
                    {
                        stageDirectory = ScanStaging.Create(dataDirectory);
                        Output = Path.Combine(stageDirectory, "snapshot.sqlite");
                        if (!cancelled && !disposed) code = RunScanner(progress, out detail);
                    }
                }
                catch (Exception ex) { code = cancelled ? 2 : 1; detail = ex.GetBaseException().Message; }
                finally { finished = true; ScheduleCleanup(); }
                if (!disposed) complete(code, detail);
            });
            worker.IsBackground = true;
            worker.Name = "Disk scan process";
            worker.Start();
        }

        private int RunScanner(Action<string> progress, out string detail)
        {
            string helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DiskScanner.exe");
            if (!File.Exists(helper)) throw new FileNotFoundException("请将 DiskScanner.exe 和管理器放在同一目录。");
            string cancelPath = Path.Combine(stageDirectory, "cancel");
            string arguments = "--output " + QuotePath(Output) + " --exclude " + QuotePath(dataDirectory) + " --cancel-file " + QuotePath(cancelPath);
            if (File.Exists(previousSnapshot)) arguments += " --previous " + QuotePath(previousSnapshot);
            string root = Environment.GetEnvironmentVariable("WDM_SCAN_ROOT");
            if (!String.IsNullOrWhiteSpace(root)) arguments += " --root " + QuotePath(Path.GetFullPath(root));
            var info = new ProcessStartInfo(helper, arguments) {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            using (var process = new Process { StartInfo = info })
            using (var job = new ScanJob())
            {
                string lastError = "";
                var stdout = new TaskCompletionSource<bool>();
                var stderr = new TaskCompletionSource<bool>();
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data == null) { stdout.TrySetResult(true); return; }
                    if (e.Data.StartsWith("ERROR\t", StringComparison.Ordinal))
                        Interlocked.Exchange(ref lastError, e.Data.Substring(6));
                    else if (!disposed && !cancelled) progress(e.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data == null) { stderr.TrySetResult(true); return; }
                    if (!String.IsNullOrWhiteSpace(e.Data)) Interlocked.Exchange(ref lastError, e.Data);
                };
                if (!process.Start()) throw new IOException("扫描器未能启动。");
                try
                {
                    job.Attach(process);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    Stopwatch sinceCancel = null;
                    while (!process.WaitForExit(100))
                    {
                        if (!cancelled && !disposed) continue;
                        if (sinceCancel == null)
                        {
                            sinceCancel = Stopwatch.StartNew();
                            try { File.WriteAllText(cancelPath, ""); }
                            catch (IOException) { Stop(process, job); }
                            catch (UnauthorizedAccessException) { Stop(process, job); }
                        }
                        else if (sinceCancel.Elapsed.TotalSeconds >= 30) Stop(process, job);
                    }
                    // A leftover descendant can retain a pipe handle after the helper
                    // exits. Close our job first and bound the final asynchronous drain.
                    job.Dispose();
                    Task.WaitAll(new Task[] { stdout.Task, stderr.Task }, 2000);
                    detail = Volatile.Read(ref lastError);
                    return process.ExitCode;
                }
                catch { Stop(process, job); throw; }
            }
        }

        internal void Cancel()
        {
            cancelled = true;
        }

        private static void Stop(Process process, ScanJob job)
        {
            job.Dispose();
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        public void Dispose()
        {
            disposed = true;
            ScheduleCleanup();
        }

        private void ScheduleCleanup()
        {
            string directory;
            lock (gate)
            {
                if (!disposed || !finished || stageDirectory == null) return;
                directory = stageDirectory;
                stageDirectory = null;
            }
            ThreadPool.QueueUserWorkItem(delegate { ScanStaging.Clean(directory); });
        }
    }
}
