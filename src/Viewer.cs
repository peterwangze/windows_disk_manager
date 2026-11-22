using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageTree
{
    internal sealed class Database : IDisposable
    {
        private const string Dll = "winsqlite3.dll";
        private IntPtr db;

        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Unicode)]
        private static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg16(IntPtr db);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr statement, int index);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr statement, int index);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text16(IntPtr statement, int index);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_blob(IntPtr statement, int index);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int index);

        internal Database(string path)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(path + "\0");
            if (sqlite3_open_v2(utf8, out db, 1, IntPtr.Zero) != 0)
            {
                string message = Error();
                Dispose();
                throw new IOException("无法读取磁盘快照。\n" + message);
            }
        }

        private string Error() { return Marshal.PtrToStringUni(sqlite3_errmsg16(db)); }

        internal void Query(string sql, Action<IntPtr> read, params long[] values)
        {
            IntPtr statement;
            if (sqlite3_prepare16_v2(db, sql, -1, out statement, IntPtr.Zero) != 0)
                throw new IOException(Error());
            try
            {
                for (int i = 0; i < values.Length; i++)
                    if (sqlite3_bind_int64(statement, i + 1, values[i]) != 0)
                        throw new IOException(Error());
                int code;
                while ((code = sqlite3_step(statement)) == 100) read(statement);
                if (code != 101) throw new IOException(Error());
            }
            finally { sqlite3_finalize(statement); }
        }
        internal static long Number(IntPtr st, int n) { return sqlite3_column_int64(st, n); }
        internal static long? MaybeNumber(IntPtr st, int n) { return sqlite3_column_type(st, n) == 5 ? (long?)null : Number(st, n); }
        internal static string Text(IntPtr st, int n) { return Marshal.PtrToStringUni(sqlite3_column_text16(st, n)) ?? ""; }
        internal static byte[] Identity(IntPtr st, int n)
        {
            if (sqlite3_column_bytes(st, n) != 16) return null;
            var result = new byte[16];
            Marshal.Copy(sqlite3_column_blob(st, n), result, 0, result.Length);
            return result;
        }
        internal bool HasColumn(string table, string column)
        {
            bool exists = false;
            Query("PRAGMA table_info(" + table + ")", delegate(IntPtr st) { if (Text(st, 1) == column) exists = true; });
            return exists;
        }
        public void Dispose()
        {
            if (db != IntPtr.Zero) { sqlite3_close(db); db = IntPtr.Zero; }
        }
    }

    public sealed class Node : INotifyPropertyChanged
    {
        internal long Id, Files;
        internal int Flags, Offset;
        internal long? Bytes, LogicalBytes;
        internal Node ParentNode;
        internal bool Loaded;
        internal Viewer Owner;
        internal long? Serial;
        internal byte[] FileId;
        internal bool ExplicitPick, Recycled;
        private bool isOpen;
        public string Name { get; set; }
        internal bool IsDirectory;
        public bool IsMore { get; set; }
        internal bool IsPlaceholder;
        internal long Total, Free;
        internal long? UniqueBytes;
        internal string Finished;
        public ObservableCollection<Node> Children { get; private set; }
        public Node() { Children = new ObservableCollection<Node>(); }
        internal bool IsRoot { get { return IsDirectory && ParentNode == null; } }
        private bool UnmeasuredDirectory { get { return IsDirectory && ((Flags & 2) != 0 || ((Flags & 5) != 0 && Files == 0 && Bytes.GetValueOrDefault() == 0)); } }
        internal bool IsRemoved { get { return Recycled || (ParentNode != null && ParentNode.IsRemoved); } }
        public bool CanReveal { get { return !IsMore && !IsPlaceholder && !IsRemoved; } }
        public bool HasPickBox { get { return !IsRoot && CanReveal; } }
        private bool Covered { get { return ParentNode != null && ParentNode.Marked; } }
        public bool Marked
        {
            get { return ExplicitPick || Covered; }
            set { if (CanPick && value != Marked) Owner.TogglePick(this); }
        }
        public bool CanPick { get { return HasPickBox && Owner != null && Owner.CanSelectNodes && Serial.HasValue && FileId != null && !Covered; } }
        public string PickLabel { get { return Covered ? "已包含在所选目录中" : "选择 " + Name; } }
        internal void UpdateSelection()
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(""));
            foreach (var child in Children) child.UpdateSelection();
        }
        public string ActionText { get { return IsDirectory ? "打开" : "定位"; } }
        public string RevealTip { get { return IsDirectory ? "在资源管理器中打开此目录" : "在资源管理器中选中此文件"; } }
        public string FullPath
        {
            get
            {
                if (ParentNode == null) return Name;
                string path = ParentNode.FullPath;
                return path + (path.EndsWith("\\", StringComparison.Ordinal) ? "" : "\\") + Name;
            }
        }
        public string Tip { get { return IsMore ? "显示按占用空间排序的下一批项目" : FullPath; } }
        internal int Depth { get { return ParentNode == null ? 0 : ParentNode.Depth + 1; } }
        public Thickness Indent { get { return new Thickness(Math.Min(Depth, 12) * 15, 0, 0, 0); } }

        private static readonly string[] GenericIcon = { "IconFile", "FileBrush" };
        private static readonly Dictionary<string, string[]> IconMap = BuildIconMap();
        private string[] typeIcon;
        private bool isHovered;

        private static Dictionary<string, string[]> BuildIconMap()
        {
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            Action<string, string, string[]> add = delegate(string icon, string brush, string[] extensions)
            {
                foreach (var extension in extensions) map[extension] = new[] { icon, brush };
            };
            add("IconImage", "ImageBrush", new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".heif", ".avif", ".raw", ".psd", ".svg" });
            add("IconVideo", "VideoBrush", new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpeg", ".mpg", ".ts", ".3gp" });
            add("IconAudio", "AudioBrush", new[] { ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma", ".opus", ".aiff", ".mid", ".midi" });
            add("IconArchive", "ArchiveBrush", new[] { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab", ".lz", ".lzma" });
            add("IconPdf", "PdfBrush", new[] { ".pdf" });
            add("IconDocument", "DocumentBrush", new[] { ".doc", ".docx", ".rtf", ".odt", ".txt", ".md", ".markdown", ".log", ".tex", ".epub", ".wps", ".pages" });
            add("IconSheet", "SheetBrush", new[] { ".xls", ".xlsx", ".xlsm", ".csv", ".tsv", ".ods", ".numbers" });
            add("IconSlides", "SlidesBrush", new[] { ".ppt", ".pptx", ".pps", ".ppsx", ".odp", ".key" });
            add("IconCode", "CodeBrush", new[] { ".cs", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".json", ".xml", ".html", ".htm", ".css", ".scss", ".sass", ".less", ".py", ".pyw", ".java", ".c", ".h", ".cpp", ".hpp", ".cc", ".go", ".rs", ".rb", ".php", ".swift", ".kt", ".kts", ".sql", ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".bash", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".config", ".gradle", ".lua", ".pl", ".r", ".vue", ".svelte", ".dart" });
            add("IconExe", "ExeBrush", new[] { ".exe", ".msi", ".dll", ".sys", ".scr", ".com", ".appx", ".msix", ".appxbundle", ".msixbundle", ".ocx", ".drv" });
            add("IconFont", "FontBrush", new[] { ".ttf", ".otf", ".ttc", ".woff", ".woff2", ".eot", ".fon" });
            add("IconDisc", "DiscBrush", new[] { ".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".vdi", ".bin", ".nrg", ".mdf" });
            add("IconLink", "LinkBrush", new[] { ".lnk", ".url", ".website", ".pif" });
            add("IconDocument", "DocumentBrush", new[] { ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb", ".pst", ".ost" });
            return map;
        }

        private string[] TypeIcon
        {
            get
            {
                if (typeIcon == null)
                {
                    string extension = Path.GetExtension(Name);
                    if (String.IsNullOrEmpty(extension) || !IconMap.TryGetValue(extension, out typeIcon)) typeIcon = GenericIcon;
                }
                return typeIcon;
            }
        }

        public Geometry IconGeometry
        {
            get { return Design.Geometry(IsMore ? "IconMore" : IsRoot ? "IconDrive" : IsDirectory ? "IconFolder" : TypeIcon[0]); }
        }
        public Brush IconBrush
        {
            get { return Design.Brush(IsMore ? "AccentBrush" : IsRoot ? "DriveBrush" : IsDirectory ? "FolderBrush" : TypeIcon[1]); }
        }
        public Brush NameBrush
        {
            get { return Design.Brush(IsMore ? "AccentTextBrush" : IsDirectory ? "InkBrush" : "InkSecondaryBrush"); }
        }
        public FontWeight Weight
        {
            get { return IsRoot || IsMore ? FontWeights.SemiBold : FontWeights.Normal; }
        }
        public bool IsHovered
        {
            get { return isHovered; }
            set
            {
                if (value == isHovered) return;
                isHovered = value;
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("IsHovered"));
            }
        }
        public string FlagLabel
        {
            get
            {
                if (IsMore || IsPlaceholder) return "";
                if (IsRemoved) return "已移入回收站";
                if (IsDirectory && (Flags & 2) != 0) return "链接";
                if ((Flags & 1) != 0) return IsDirectory ? "部分可读" : "占用未知";
                if (IsDirectory && (Flags & 4) != 0) return "部分可读";
                if (!IsDirectory && (Flags & 2) != 0) return "重解析点";
                return "";
            }
        }
        public Visibility FlagVisibility
        {
            get { return FlagLabel.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
        }
        private string FlagTone
        {
            get
            {
                string label = FlagLabel;
                if (label == "已移入回收站") return "Danger";
                if (label == "链接" || label == "重解析点") return "Accent";
                return "Warning";
            }
        }
        public Brush FlagBackground
        {
            get { return Design.Brush(FlagVisibility == Visibility.Visible ? FlagTone + "SoftBrush" : "TransparentBrush"); }
        }
        public Brush FlagBorder
        {
            get { return Design.Brush(FlagVisibility == Visibility.Visible ? FlagTone + "BorderBrush" : "TransparentBrush"); }
        }
        public Brush FlagForeground
        {
            get { return Design.Brush(FlagVisibility == Visibility.Visible ? FlagTone + "TextBrush" : "InkMutedBrush"); }
        }
        public string SizeText { get { return IsMore || IsPlaceholder ? "" : UnmeasuredDirectory ? "未统计" : FormatSize(Bytes); } }
        public string LengthText { get { return IsMore || IsPlaceholder ? "" : UnmeasuredDirectory ? "未统计" : FormatSize(LogicalBytes); } }
        public string BytesTip { get { return !UnmeasuredDirectory && Bytes.HasValue ? Bytes.Value.ToString("N0") + " 字节已分配" : "无法读取占用空间"; } }
        public string LogicalTip { get { return !UnmeasuredDirectory && LogicalBytes.HasValue ? LogicalBytes.Value.ToString("N0") + " 字节文件内容" : "未知"; } }
        public bool ShowShare { get { return CanReveal && !UnmeasuredDirectory && Bytes.HasValue && ParentNode != null && ParentNode.Bytes.GetValueOrDefault() > 0; } }
        private double Fraction { get { return ShowShare ? Math.Min(1d, Math.Max(0d, (double)Bytes.Value / ParentNode.Bytes.Value)) : 0d; } }
        public GridLength Filled { get { return new GridLength(Fraction, GridUnitType.Star); } }
        public GridLength Unfilled { get { return new GridLength(1d - Fraction, GridUnitType.Star); } }
        public string PercentText { get { return Fraction > 0 && Fraction < 0.001 ? "<0.1%" : Fraction.ToString("0.#%", CultureInfo.InvariantCulture); } }
        public Brush BarBrush { get { return Design.Brush(IsDirectory ? "DirectoryBarBrush" : "FileBarBrush"); } }
        public static string FormatSize(long? value)
        {
            if (!value.HasValue) return "未知";
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
            double size = value.Value;
            int unit = 0;
            while (size >= 1024d && unit < units.Length - 1) { size /= 1024d; unit++; }
            return size.ToString(unit == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + units[unit];
        }
        public bool IsOpen
        {
            get { return isOpen; }
            set
            {
                if (value == isOpen) return;
                isOpen = value;
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("IsOpen"));
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        internal void SetExpandable()
        {
            if (IsDirectory && (Flags & 2) == 0)
                Children.Add(new Node { IsPlaceholder = true, Name = "" });
        }
    }

    internal sealed class DriveCardView
    {
        public string Title { get; set; }
        public string UsageText { get; set; }
        public string FreeText { get; set; }
        public string PercentText { get; set; }
        public string Tip { get; set; }
        public Geometry ArcGeometry { get; set; }
        public Brush ArcBrush { get; set; }
        public Brush PercentBrush { get; set; }
        public Brush IconBackground { get; set; }
        public Brush IconBrush { get; set; }
    }

    internal sealed class SummaryCardView
    {
        public string CountText { get; set; }
        public string UsedText { get; set; }
        public string TotalText { get; set; }
        public GridLength Filled { get; set; }
        public GridLength Unfilled { get; set; }
        public Brush BarBrush { get; set; }
    }

    internal static class Explorer
    {
        [DllImport("shell32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr item, uint attributes, IntPtr resultAttributes);
        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);
        [DllImport("shell32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        private static extern IntPtr ShellExecuteW(IntPtr window, string verb, string path, string arguments, string directory, int show);
        internal static void Show(Node item)
        {
            if (item.IsDirectory)
            {
                if (!Directory.Exists(item.FullPath)) throw new DirectoryNotFoundException("目录已移动、类型已变化或当前无法访问。");
                long result = ShellExecuteW(IntPtr.Zero, "explore", item.FullPath, null, null, 1).ToInt64();
                if (result <= 32) throw new Win32Exception((int)result);
                return;
            }
            IntPtr pidl;
            Marshal.ThrowExceptionForHR(SHParseDisplayName(item.FullPath, IntPtr.Zero, out pidl, 0, IntPtr.Zero));
            try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0)); }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
    }

    internal sealed partial class Viewer : IDisposable
    {
        private const int PageSize = 160;
        private Database db;
        private readonly Window window;
        private readonly TreeView tree;
        private readonly TextBlock selection;
        private readonly List<Node> roots = new List<Node>();
        private Node hovered;

        internal Viewer(Window window, string dataDirectory)
        {
            this.window = window;
            this.dataDirectory = dataDirectory;
            tree = (TreeView)window.FindName("UsageTree");
            selection = (TextBlock)window.FindName("Selection");
            selection.Text = "选择一项，查看完整路径、空间和文件数。";
            selection.ToolTip = "选择一项，查看完整路径、空间和文件数。";
            tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(Expanded));
            tree.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(ActionClick));
            tree.MouseMove += delegate(object sender, MouseEventArgs args) { SetHovered(HoverNode(args)); };
            tree.MouseLeave += delegate { SetHovered(null); };
            tree.SelectedItemChanged += delegate(object sender, RoutedPropertyChangedEventArgs<object> args)
            {
                Node item = args.NewValue as Node;
                if (item == null || !item.CanReveal) return;
                string details = item.FullPath + (item.IsDirectory && item.SizeText != "未统计" ? "    ·    " + item.Files.ToString("N0") + " 个文件" : "");
                if (item.IsRoot && item.UniqueBytes.HasValue)
                    details += "    ·    已统计文件占用（硬链接去重） " + Node.FormatSize(item.UniqueBytes);
                if (item.FlagLabel != "") details += "    ·    " + item.FlagLabel;
                if (item.IsDirectory && (item.Flags & 8) != 0) details += "    ·    包含已跳过的目录链接";
                selection.Text = details;
                selection.ToolTip = details;
            };
            tree.MouseDoubleClick += delegate(object sender, MouseButtonEventArgs e)
            {
                Node item = tree.SelectedItem as Node;
                if (item != null && item.IsMore) { LoadPage(item.ParentNode); e.Handled = true; }
            };
            AttachControls();
        }

        private Node HoverNode(MouseEventArgs args)
        {
            DependencyObject element = tree.InputHitTest(args.GetPosition(tree)) as DependencyObject;
            while (element != null)
            {
                var container = element as TreeViewItem;
                if (container != null)
                {
                    if (container.IsSelected) return null;
                    return container.DataContext as Node;
                }
                if (!(element is Visual) && !(element is System.Windows.Media.Media3D.Visual3D)) return null;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private void SetHovered(Node node)
        {
            if (ReferenceEquals(node, hovered)) return;
            if (hovered != null) hovered.IsHovered = false;
            hovered = node;
            if (hovered != null) hovered.IsHovered = true;
        }

        private List<Node> ReadRoots(Database source, out bool identities)
        {
            identities = source.HasColumn("volumes","serial") && source.HasColumn("dirs","fsid") && source.HasColumn("entries","fsid");
            var result = new List<Node>();
            source.Query("SELECT d.id,d.name,d.bytes,d.logical_bytes,d.files,d.flags,v.total_bytes,v.free_bytes,v.finished," +
                (source.HasColumn("volumes","unique_bytes") ? "v.unique_bytes" : "NULL") + "," + (identities ? "v.serial" : "NULL") +
                " FROM dirs d JOIN volumes v ON v.root_id=d.id WHERE d.parent IS NULL ORDER BY d.name",
                delegate(IntPtr s)
                {
                    Node item = new Node { Id = Database.Number(s,0), Name = Database.Text(s,1), Bytes = Database.MaybeNumber(s,2),
                        LogicalBytes = Database.MaybeNumber(s,3), Files = Database.Number(s,4), Flags = (int)Database.Number(s,5),
                        Total = Database.Number(s,6), Free = Database.Number(s,7), Finished = Database.Text(s,8), UniqueBytes = Database.MaybeNumber(s,9),
                        Serial = Database.MaybeNumber(s,10), Owner = this, IsDirectory = true };
                    item.SetExpandable();
                    result.Add(item);
                });
            if (result.Count == 0) throw new InvalidDataException("快照中没有可用的磁盘。");
            return result;
        }

        private void Display(List<Node> incoming)
        {
            picked.Clear();
            tree.ItemsSource = null;
            roots.Clear();
            roots.AddRange(incoming);
            ((WrapPanel)window.FindName("DriveCards")).Children.Clear();
            ((FrameworkElement)window.FindName("Empty")).Visibility = Visibility.Collapsed;
            tree.ItemsSource = roots;
            foreach (var item in roots) AddCard(item);
            AddSummaryCard(roots);
            string finish = roots.Select(item => item.Finished).OrderBy(x => x, StringComparer.Ordinal).Last();
            DateTimeOffset time;
            if (DateTimeOffset.TryParse(finish, out time)) finish = time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            ((TextBlock)window.FindName("Stamp")).Text = "最近扫描  " + finish;
            ShowRoot(roots[0]);
            SetControls();
        }

        private void AddCard(Node item)
        {
            var panel = (WrapPanel)window.FindName("DriveCards");
            long used = Math.Max(0, item.Total - item.Free);
            double fraction = item.Total > 0 ? Math.Min(1d, Math.Max(0d, (double)used / item.Total)) : 0d;
            string tone = fraction >= 0.92 ? "Danger" : fraction >= 0.8 ? "Warning" : "Accent";
            string driveName;
            if (item.Name.Length == 3 && item.Name[1] == ':') driveName = item.Name.Substring(0, 1) + " 盘";
            else
            {
                driveName = Path.GetFileName(item.Name.TrimEnd('\\'));
                if (String.IsNullOrEmpty(driveName)) driveName = item.Name;
            }
            var card = new DriveCardView
            {
                Title = driveName,
                UsageText = item.Total > 0
                    ? "已用 " + Node.FormatSize(used) + " · 共 " + Node.FormatSize(item.Total)
                    : "容量未知 · 磁盘可能未联机",
                FreeText = item.Total > 0 ? "可用 " + Node.FormatSize(item.Free) : "系统未报告此磁盘的容量",
                PercentText = item.Total > 0 ? fraction.ToString("0%", CultureInfo.InvariantCulture) : "—",
                Tip = item.Name + (item.Total > 0
                    ? "   已用 " + Node.FormatSize(used) + "，可用 " + Node.FormatSize(item.Free) + "，总计 " + Node.FormatSize(item.Total)
                    : "   系统未报告容量"),
                ArcGeometry = UiKit.RingArc(30, 27.5, fraction),
                ArcBrush = Design.Brush(tone + "GradientBrush"),
                PercentBrush = Design.Brush(tone + "TextBrush"),
                IconBackground = Design.Brush(tone == "Accent" ? "AccentSoftBrush" : tone + "SoftBrush"),
                IconBrush = Design.Brush(tone + "Brush")
            };
            var button = new Button
            {
                Content = card,
                ContentTemplate = (DataTemplate)window.FindResource("DriveCardTemplate"),
                Style = (Style)window.FindResource("DriveCardButton"),
                ToolTip = "展开并跳转到 " + item.Name
            };
            AutomationProperties.SetName(button, "跳到 " + item.Name);
            AutomationProperties.SetAutomationId(button, "Drive" + item.Name.Substring(0, 1));
            button.Click += delegate { ShowRoot(item); };
            panel.Children.Add(button);
        }

        private void AddSummaryCard(List<Node> items)
        {
            var panel = (WrapPanel)window.FindName("DriveCards");
            long total = items.Sum(n => Math.Max(0, n.Total));
            long free = items.Sum(n => Math.Max(0, n.Free));
            long files = items.Sum(n => n.Files);
            double fraction = total > 0 ? Math.Min(1d, Math.Max(0d, (double)(total - free) / total)) : 0d;
            string tone = fraction >= 0.92 ? "Danger" : fraction >= 0.8 ? "Warning" : "Accent";
            var summary = new SummaryCardView
            {
                CountText = items.Count.ToString("N0") + " 块磁盘 · " + files.ToString("N0") + " 个文件",
                UsedText = "已用 " + Node.FormatSize(total - free),
                TotalText = "总容量 " + Node.FormatSize(total) + " · 可用 " + Node.FormatSize(free),
                Filled = new GridLength(Math.Max(0.0001d, fraction), GridUnitType.Star),
                Unfilled = new GridLength(Math.Max(0.0001d, 1d - fraction), GridUnitType.Star),
                BarBrush = Design.Brush(tone + "GradientBrush")
            };
            var border = new Border
            {
                Style = (Style)window.FindResource("SummaryCard"),
                Child = new ContentControl
                {
                    Content = summary,
                    ContentTemplate = (DataTemplate)window.FindResource("SummaryCardTemplate")
                },
                ToolTip = "所有已扫描磁盘的容量与文件数汇总"
            };
            AutomationProperties.SetAutomationId(border, "DriveSummary");
            AutomationProperties.SetName(border, "空间概览");
            panel.Children.Add(border);
        }

        private void ShowRoot(Node item)
        {
            // Bring the second volume into view even when the first tree has many expanded descendants.
            if (!item.Loaded) LoadPage(item);
            tree.ApplyTemplate();
            var rootPanel = FindVisual<VirtualizingStackPanel>(tree);
            if (rootPanel != null) rootPanel.BringIndexIntoViewPublic(roots.IndexOf(item));
            item.IsOpen = true;
            tree.UpdateLayout();
            var container = tree.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            if (container == null)
            {
                tree.ApplyTemplate();
                tree.UpdateLayout();
                var scroll = FindVisual<ScrollViewer>(tree);
                if (scroll != null)
                {
                    if (item == roots[0]) scroll.ScrollToTop(); else scroll.ScrollToBottom();
                    tree.UpdateLayout();
                }
                container = tree.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            }
            if (container != null)
            {
                container.IsSelected = true;
                var header = container.Template.FindName("PART_Header", container) as FrameworkElement;
                if (header != null)
                {
                    // Let WPF place the header and its children together. Writing a pixel
                    // offset back during layout can endlessly re-anchor the virtualized tree.
                    double height = Math.Max(header.ActualHeight, tree.ActualHeight);
                    header.BringIntoView(new Rect(0, 0, header.ActualWidth, height));
                }
            }
        }

        private static T FindVisual<T>(DependencyObject source) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
            {
                var child = VisualTreeHelper.GetChild(source, i);
                if (child is T) return (T)child;
                var found = FindVisual<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void Expanded(object sender, RoutedEventArgs e)
        {
            var container = e.OriginalSource as TreeViewItem;
            var item = container == null ? null : container.DataContext as Node;
            if (item != null && item.IsDirectory && !item.Loaded) LoadPage(item);
        }

        private void LoadPage(Node item)
        {
            if (db == null) return;
            try { AppendPage(item, ReadPage(db, item, hasIdentities)); }
            catch (Exception ex) { MessageBox.Show(window, "无法读取此目录。\n" + ex.Message, "Windows 磁盘空间管理器"); }
        }

        private List<Node> ReadPage(Database source, Node item, bool identities)
        {
            string identity = identities ? "fsid" : "NULL";
            string sql = "SELECT id,1 AS kind,name,bytes,logical_bytes,flags,files," + identity + " FROM dirs WHERE parent=?1 " +
                "UNION ALL SELECT id,0 AS kind,name,bytes,logical_bytes,flags,0 AS files," + identity + " FROM entries WHERE parent=?1 " +
                "ORDER BY bytes DESC,kind DESC,name,id LIMIT ?2 OFFSET ?3";
            var next = new List<Node>();
            source.Query(sql, delegate(IntPtr s)
            {
                var child = new Node { Id = Database.Number(s,0), IsDirectory = Database.Number(s,1) == 1,
                    Name = Database.Text(s,2), Bytes = Database.MaybeNumber(s,3), LogicalBytes = Database.MaybeNumber(s,4),
                    Flags = (int)Database.Number(s,5), Files = Database.Number(s,6), ParentNode = item, FileId = Database.Identity(s,7),
                    Serial = item.Serial, Owner = this };
                child.SetExpandable();
                next.Add(child);
            }, item.Id, PageSize + 1, item.Offset);
            return next;
        }

        private static void AppendPage(Node item, List<Node> next)
        {
            var oldMarker = item.Children.LastOrDefault(n => n.IsMore || n.IsPlaceholder);
            // Insert before the placeholder to keep a populated directory expandable throughout the change.
            int index = oldMarker == null ? item.Children.Count : item.Children.IndexOf(oldMarker);
            int count = Math.Min(PageSize, next.Count);
            for (int i = 0; i < count; i++) item.Children.Insert(index++, next[i]);
            if (oldMarker != null) item.Children.Remove(oldMarker);
            item.Offset += count;
            item.Loaded = true;
            if (next.Count > PageSize)
                item.Children.Add(new Node { IsMore = true, Name = "显示更多项目", ParentNode = item });
        }

        private void ActionClick(object sender, RoutedEventArgs e)
        {
            if (scan != null) return;
            var button = e.OriginalSource as Button;
            if (button == null) return;
            var item = button.DataContext as Node;
            if (item == null) return;
            if (Equals(button.Tag, "more")) { e.Handled = true; LoadPage(item.ParentNode); return; }
            if (!Equals(button.Tag, "reveal") || !item.CanReveal) return;
            e.Handled = true;
            try { Explorer.Show(item); }
            catch (Exception ex) { MessageBox.Show(window, "资源管理器未能打开展示的路径。文件可能已移动或需要访问权限。\n\n" + item.FullPath + "\n" + ex.Message, "Windows 磁盘空间管理器", MessageBoxButton.OK, MessageBoxImage.Information); }
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            try
            {
                string data = Environment.GetEnvironmentVariable("WDM_DATA_DIR");
                if (String.IsNullOrWhiteSpace(data)) data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsDiskManager");
                data = Path.GetFullPath(data);
                Directory.CreateDirectory(data);
                using (var instance = InstanceLock.Acquire(data))
                using (var theme = Assembly.GetExecutingAssembly().GetManifestResourceStream("Theme.xaml"))
                using (var xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("Viewer.xaml"))
                {
                    if (instance == null) return;
                    var app = new Application();
                    app.Resources = (ResourceDictionary)XamlReader.Load(theme);
                    var window = (Window)XamlReader.Load(xaml);
                    window.Width = Math.Min(window.Width, SystemParameters.WorkArea.Width * 0.94);
                    window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height * 0.92);
                    using (var viewer = new Viewer(window, data)) app.Run(window);
                }
            }
            catch (Exception ex) { MessageBox.Show("无法打开 Windows 磁盘空间管理器。\n\n" + ex.GetBaseException().Message, "Windows 磁盘空间管理器", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}
