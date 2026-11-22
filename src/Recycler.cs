using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UsageTree
{
    public sealed class RecycleTarget
    {
        public string Path;
        public bool IsDirectory;
        public byte[] FileId;
        public long VolumeSerial;
    }

    public sealed class RecycleResult
    {
        public string Path;
        public bool Success;
        public string Message;
    }

    public static class Recycler
    {
        static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        const int DirectoryAttribute = 0x10, ReparseAttribute = 0x400;
        const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4;

        public static List<RecycleResult> Recycle(IList<RecycleTarget> targets, IntPtr owner)
        {
            var results = new List<RecycleResult>();
            if (targets == null) return results;
            var valid = new List<int>();
            for (int i = 0; i < targets.Count; i++)
            {
                var result = new RecycleResult { Path = targets[i] == null ? null : targets[i].Path };
                results.Add(result);
                try { ValidatePath(result.Path); valid.Add(i); }
                catch (Exception ex) { result.Message = ex.Message; }
            }
            // The tree preserves exact on-disk casing, including case-sensitive NTFS directories.
            var recycled = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool cancelled = false;
            // Process the shallowest paths first; a successfully recycled folder covers its descendants.
            foreach (int i in valid.OrderBy(index => results[index].Path.Length))
            {
                var target = targets[i];
                var result = results[i];
                if (cancelled) { result.Message = "批量操作已取消，此项尚未处理。"; continue; }
                try
                {
                    if (AlreadyHandled(target.Path, recycled))
                    {
                        result.Success = true;
                        result.Message = "已随所选项目移入回收站";
                        continue;
                    }
                    ProtectApplication(target.Path);
                    bool coversChildren;
                    using (var guards = new PathGuards(target))
                    {
                        RecycleNative(target, guards);
                        coversChildren = target.IsDirectory && !guards.LeafIsReparse;
                    }
                    recycled[target.Path] = coversChildren;
                    result.Success = true;
                    result.Message = "已移入回收站";
                }
                catch (OperationCanceledException ex) { result.Message = ex.Message; cancelled = true; }
                catch (Exception ex) { result.Message = ex.Message; }
            }
            return results;
        }

        static void ValidatePath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length < 4 || path[1] != ':' || path[2] != '\\' ||
                !((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')))
                throw new IOException("请选择本地磁盘上的文件或子目录。磁盘根目录不能移入回收站。");
            string[] parts = path.Substring(3).Split('\\');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.Any(c => c < ' ' || "<>:\"/|?*".IndexOf(c) >= 0))
                    throw new IOException("路径含有不明确的文件名或通配符。");
                string stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (stem == "CON" || stem == "AUX" || stem == "PRN" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$" ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".IndexOf(stem[3]) >= 0))
                    throw new IOException("设备名称不能移入回收站。");
            }
            if (PathComparer.Equals(parts[0], "$Recycle.Bin")) throw new IOException("所选项目位于回收站中。");
            uint type = GetDriveTypeW(path.Substring(0, 3));
            if (type != 2 && type != 3) throw new IOException("此位置不支持本地回收站。");
        }

        static bool AlreadyHandled(string path, Dictionary<string, bool> recycled)
        {
            if (recycled.ContainsKey(path)) return true;
            for (int slash = path.LastIndexOf('\\'); slash > 2; slash = path.LastIndexOf('\\', slash - 1))
            {
                bool coversChildren;
                if (recycled.TryGetValue(path.Substring(0, slash), out coversChildren) && coversChildren) return true;
            }
            return false;
        }

        static bool Within(string path, string parent)
        {
            return PathComparer.Equals(path, parent) || path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
        }

        static void ProtectApplication(string path)
        {
            string app = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string data = Environment.GetEnvironmentVariable("WDM_DATA_DIR");
            data = Path.GetFullPath(String.IsNullOrWhiteSpace(data) ? Path.Combine(local, "WindowsDiskManager") : data).TrimEnd('\\');
            string installed = Path.Combine(local, @"Programs\WindowsDiskManager");
            if (Within(path, data) || Within(data, path) || Within(path, installed) || Within(installed, path))
                throw new IOException("此项目包含程序或扫描数据，已跳过。");
            foreach (string name in new[] { "WindowsDiskManager.exe", "WindowsDiskManager.exe.config", "DiskScanner.exe", "DiskScanner.exe.config" })
                if (Within(Path.Combine(app, name), path)) throw new IOException("此项目包含正在使用的程序，已跳过。");
            Assembly entry = Assembly.GetEntryAssembly();
            if (entry != null && !String.IsNullOrEmpty(entry.Location) && Within(entry.Location, path))
                throw new IOException("此项目包含正在使用的程序，已跳过。");
        }

        // Keep every directory on the path open through the shell transfer. Denying write and
        // delete sharing prevents a directory being renamed or retargeted into a junction.
        sealed class PathGuards : IDisposable
        {
            readonly List<SafeFileHandle> handles = new List<SafeFileHandle>();
            internal bool LeafIsReparse;

            internal PathGuards(RecycleTarget target)
            {
                try
                {
                    string path = target.Path;
                    for (int end = 2; ; )
                    {
                        bool leaf = end == path.Length;
                        string prefix = end == 2 ? path.Substring(0, 3) : path.Substring(0, end);
                        var h = Open(prefix, leaf ? ShareRead | ShareWrite | ShareDelete : ShareRead);
                        handles.Add(h);
                        int attributes = Attributes(h);
                        if (leaf)
                        {
                            LeafIsReparse = (attributes & ReparseAttribute) != 0;
                            if (((attributes & DirectoryAttribute) != 0) != target.IsDirectory)
                                throw new IOException("项目类型已变化，请重新扫描。");
                            CheckIdentity(h, target);
                            var final = HandlePath(h);
                            ValidatePath(final);
                            ProtectApplication(final);
                            break;
                        }
                        if ((attributes & DirectoryAttribute) == 0 || (attributes & ReparseAttribute) != 0)
                            throw new IOException("上级目录已变化或为重解析点，请重新扫描。");
                        int next = path.IndexOf('\\', end + 1);
                        end = next < 0 ? path.Length : next;
                    }
                }
                catch { Dispose(); throw; }
            }

            internal void CheckCurrent(RecycleTarget target)
            {
                using (var current = Open(target.Path, ShareRead | ShareWrite | ShareDelete)) CheckIdentity(current, target);
            }
            public void Dispose()
            {
                for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
                handles.Clear();
            }
        }

        static SafeFileHandle Open(string path, uint share)
        {
            var h = CreateFileW(@"\\?\" + path, 0x80, share, IntPtr.Zero, 3, 0x02300000, IntPtr.Zero);
            if (h.IsInvalid) { int err = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(err); }
            return h;
        }

        static int Attributes(SafeFileHandle h)
        {
            byte[] buffer = new byte[40];
            if (!GetFileInformationByHandleEx(h, 0, buffer, buffer.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return BitConverter.ToInt32(buffer, 32);
        }

        static void CheckIdentity(SafeFileHandle h, RecycleTarget target)
        {
            if (target.FileId == null || target.FileId.Length != 16 || !target.FileId.Any(b => b != 0))
                throw new IOException("扫描记录缺少文件标识，请重新扫描。");
            byte[] buffer = new byte[24];
            if (!GetFileInformationByHandleEx(h, 18, buffer, buffer.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (target.VolumeSerial != BitConverter.ToInt64(buffer, 0) || !target.FileId.SequenceEqual(buffer.Skip(8)))
                throw new IOException("此路径的项目已经更换，请重新扫描。");
        }

        static string HandlePath(SafeFileHandle handle)
        {
            var name = new StringBuilder(512);
            uint length = GetFinalPathNameByHandleW(handle, name, name.Capacity, 0);
            if (length >= name.Capacity)
            {
                name = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandleW(handle, name, name.Capacity, 0);
            }
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            string canonical = name.ToString();
            if (!canonical.StartsWith(@"\\?\", StringComparison.Ordinal)) throw new IOException("无法验证项目的实际路径。");
            return canonical.Substring(4);
        }

        // ITransferSource.RecycleItem is a dedicated transfer into the Recycle Bin. Its sibling
        // RemoveItem (permanent deletion) is deliberately absent from this interop interface.
        static void RecycleNative(RecycleTarget target, PathGuards guards)
        {
            Guid iidItem = typeof(IShellItem).GUID, iidTransfer = typeof(ITransferSource).GUID;
            Guid recycleFolder = new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
            Guid transferHandler = new Guid("D5E346A1-F753-4932-B403-4574800E2498");
            IShellItem item = null, parent = null, bin = null, recycled = null;
            ITransferSource source = null;
            try
            {
                string shellPath = target.Path.Length >= 260 ? @"\\?\" + target.Path : target.Path;
                CheckHr(SHCreateItemFromParsingName(shellPath, IntPtr.Zero, ref iidItem, out item));
                item.GetParent(out parent);
                parent.BindToHandler(IntPtr.Zero, ref transferHandler, ref iidTransfer, out source);
                CheckHr(SHGetKnownFolderItem(ref recycleFolder, 0, IntPtr.Zero, ref iidItem, out bin));
                guards.CheckCurrent(target);
                int hr = source.RecycleItem(item, bin, 0x80, out recycled);
                CheckHr(hr);
                if (recycled == null) throw new IOException("Windows 未确认回收站中的目标项目。请重新扫描以核实状态。");
                // Verify the shell's destination identity too: a concurrently replaced name
                // must never be reported as successful disposal of the scanned object.
                IntPtr location = IntPtr.Zero;
                try
                {
                    recycled.GetDisplayName(0x80058000, out location); // SIGDN_FILESYSPATH
                    string destination = Marshal.PtrToStringUni(location);
                    if (destination.StartsWith(@"\\?\", StringComparison.Ordinal)) destination = destination.Substring(4);
                    using (var moved = Open(destination, ShareRead | ShareWrite | ShareDelete)) CheckIdentity(moved, target);
                }
                catch (Exception ex) { throw new IOException("Windows 已执行回收，但未能核对目标身份。请重新扫描。" + ex.Message, ex); }
                finally { if (location != IntPtr.Zero) Marshal.FreeCoTaskMem(location); }
            }
            finally
            {
                Release(recycled); Release(source); Release(bin); Release(parent); Release(item);
            }
        }

        static void CheckHr(int hr)
        {
            switch (unchecked((uint)hr))
            {
                case 0x80270036: case 0x8027003A: throw new IOException("此位置的回收站不可用。");
                case 0x80270037: throw new IOException("项目超过此磁盘的回收站容量。");
                case 0x80270038: throw new IOException("项目路径超出回收站支持的长度。");
                case 0x800704C7: case 0x80270000: case 0x80270001: case 0x00270005: throw new OperationCanceledException("操作已取消。");
            }
            Marshal.ThrowExceptionForHR(hr);
        }
        static void Release(object value) { if (value != null) Marshal.ReleaseComObject(value); }

        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern uint GetDriveTypeW(string root);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, [Out] byte[] data, int size);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, int size, uint flags);
        [DllImport("shell32", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
        [DllImport("shell32")] static extern int SHGetKnownFolderItem(ref Guid id, uint flags, IntPtr token, ref Guid iid, out IShellItem item);

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out ITransferSource source);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint kind, out IntPtr name);
        }
        [ComImport, Guid("00ADB003-BDE9-45C6-8E29-D09F9353E108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITransferSource
        {
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetProperties(IntPtr properties);
            void OpenItem(IShellItem item, uint flags, ref Guid iid, out IntPtr result);
            void MoveItem(IShellItem source, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IShellItem moved);
            [PreserveSig] int RecycleItem(IShellItem item, IShellItem destination, uint flags, out IShellItem recycled);
        }
    }
}
