using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageTree
{
    internal sealed partial class Viewer
    {
        private readonly string dataDirectory;
        private string Snapshot { get { return Path.Combine(dataDirectory, "disk-usage.sqlite"); } }
        private string StaleMarker { get { return Path.Combine(dataDirectory, "refresh-required"); } }
        private string activePath;
        private bool hasIdentities, closing, recycling, stale, disposed;
        private ScanRun scan;
        private readonly HashSet<Node> picked = new HashSet<Node>();
        private string lastReport;
        private readonly Dictionary<string,Tuple<long,long>> counters = new Dictionary<string,Tuple<long,long>>(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch scanTime = new Stopwatch();
        private readonly DispatcherTimer scanClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private bool committingScan;
        internal bool CanSelectNodes { get { return !recycling && !updateInProgress && hasIdentities; } }
        internal bool AllowPick { get { return scan == null && CanSelectNodes; } }

        private T Control<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
        private void AttachControls()
        {
            AttachUpdates();
            scanClock.Tick += delegate { ShowElapsed(); };
            Control<Button>("Refresh").Click += delegate { BeginRefresh(); };
            Control<Button>("CancelScan").Click += delegate { CancelRefresh(); };
            Control<Button>("ClearSelection").Click += delegate { ClearPicks(); };
            Control<Button>("RecycleSelection").Click += delegate { BeginRecycle(); };
            Control<Button>("RecycleReport").Click += delegate { PathDialog("回收站处理结果", lastReport, false); };
            window.PreviewKeyDown += delegate(object sender, KeyEventArgs args) {
                if (args.Key == Key.F5) { BeginRefresh(); args.Handled = true; }
                else if (args.Key == Key.Escape && scan != null) { CancelRefresh(); args.Handled = true; }
            };
            window.Closing += OnClosing;
            window.Loaded += delegate
            {
                ScanStaging.CleanAbandoned(dataDirectory);
                stale = File.Exists(StaleMarker);
                string path = File.Exists(Snapshot) ? Snapshot : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "disk-usage.sqlite");
                bool local = String.Equals(path, Snapshot, StringComparison.OrdinalIgnoreCase);
                if (File.Exists(path))
                {
                    try { OpenExisting(path); }
                    catch (Exception ex) { SetStatus("现有快照无法读取：" + ex.GetBaseException().Message); }
                }
                if (db == null || !local || stale) BeginRefresh();
                else SetStatus(hasIdentities ? "展开目录查看占用，勾选文件或目录可移入回收站。" : "当前为旧版快照。点击刷新即可启用勾选操作。");
                SetControls();
            };
        }

        private void OpenExisting(string path)
        {
            var source = new Database(path);
            try
            {
                bool ids;
                var incoming = ReadRoots(source, out ids);
                db = source;
                activePath = path;
                hasIdentities = ids;
                Display(incoming);
            }
            catch { source.Dispose(); db = null; throw; }
        }

        private void SetStatus(string text)
        {
            string suffix = stale && db != null ? "  当前快照可能包含已移走的项目。" : "";
            Control<TextBlock>("ScanStatus").Text = text + suffix;
        }
        private void SetEmpty(string title, string note)
        {
            Control<TextBlock>("EmptyTitle").Text = title;
            Control<TextBlock>("EmptyNote").Text = note;
            Control<FrameworkElement>("Empty").Visibility = Visibility.Visible;
        }
        private void SetControls()
        {
            bool scanning = scan != null;
            bool busy = scanning || recycling || updateInProgress;
            Control<Button>("Refresh").IsEnabled = !busy;
            tree.IsEnabled = !scanning;
            Control<WrapPanel>("DriveCards").IsEnabled = !scanning;
            Control<Button>("CheckUpdates").IsEnabled = !scanning;
            Control<Button>("RecycleReport").IsEnabled = !scanning;
            Control<FrameworkElement>("ScanActivity").Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
            var cancel = Control<Button>("CancelScan");
            cancel.IsEnabled = scanning && !scan.Cancelled && !committingScan;
            Control<Button>("ClearSelection").IsEnabled = picked.Count != 0 && !busy;
            Control<Button>("RecycleSelection").IsEnabled = picked.Count != 0 && AllowPick;
            Control<TextBlock>("SelectionCount").Text = "已选 " + picked.Count.ToString("N0") + " 项";
            Control<System.Windows.Shapes.Ellipse>("StatusDot").Fill = Design.Brush(scan != null ? "AccentBrush" : stale ? "WarningBrush" : "InkFaintBrush");
            if (updateDialog != null) updateDialog.RefreshActions();
        }
        private void UpdatePicks()
        {
            foreach (var root in roots) root.UpdateSelection();
            SetControls();
        }

        private void BeginRefresh()
        {
            if (scan != null || recycling || updateInProgress || closing) return;
            counters.Clear();
            var run = new ScanRun(dataDirectory);
            scan = run;
            scanTime.Restart();
            ShowElapsed();
            scanClock.Start();
            if (db == null) SetEmpty("正在建立磁盘快照", "扫描完成后，这里会显示各个磁盘的文件树。");
            Control<TextBlock>("ScanStage").Text = "正在启动扫描器…";
            var counts = Control<TextBlock>("ScanCounts");
            counts.Text = "正在等待读取目录和文件…";
            counts.ToolTip = null;
            SetControls();
            Control<Button>("CancelScan").Focus();
            try
            {
                run.Start(
                    delegate(string line) { window.Dispatcher.BeginInvoke(new Action(delegate { ShowProgress(run, line); })); },
                    delegate(int code, string detail) { window.Dispatcher.BeginInvoke(new Action(delegate { EndRefresh(run, code, detail); })); });
            }
            catch (Exception ex) { EndRefresh(run, 1, ex.GetBaseException().Message); }
        }
        private void ShowElapsed()
        {
            var elapsed = scanTime.Elapsed;
            Control<TextBlock>("ScanElapsed").Text = "已用 " + elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
        }
        private void ShowProgress(ScanRun run, string line)
        {
            if (scan != run || run.Cancelled || committingScan || disposed) return;
            string[] words = line.Split('\t');
            if (words[0] == "SCAN" && words.Length >= 4)
            {
                long dirs, files;
                if (!Int64.TryParse(words[2], out dirs) || !Int64.TryParse(words[3], out files) || dirs < 0 || files < 0) return;
                counters[words[1]] = Tuple.Create(dirs, files);
                Control<TextBlock>("ScanStage").Text = "正在扫描 " + words[1];
                var counts = Control<TextBlock>("ScanCounts");
                counts.Text = String.Format("已扫描 {0:N0} 个目录  ·  {1:N0} 个文件",
                    counters.Values.Sum(n => n.Item1), counters.Values.Sum(n => n.Item2));
                counts.ToolTip = String.Join(Environment.NewLine, counters.Select(n =>
                    String.Format("{0}   {1:N0} 个目录 / {2:N0} 个文件", n.Key, n.Value.Item1, n.Value.Item2)));
            }
            else if (words[0] == "STAGE" && words.Length >= 2) Control<TextBlock>("ScanStage").Text = words[1];
        }
        private void CancelRefresh()
        {
            if (scan == null || committingScan || scan.Cancelled) return;
            scan.Cancel();
            Control<TextBlock>("ScanStage").Text = "正在取消扫描…";
            SetControls();
        }
        private async void EndRefresh(ScanRun run, int code, string reason)
        {
            if (scan != run || disposed) return;
            try
            {
                if (run.Cancelled || code == 2) SetStatus("已取消扫描。点击刷新可重新开始。");
                else if (code != 0) SetStatus("扫描失败，点击刷新重试。" + (String.IsNullOrWhiteSpace(reason) ? "" : "  " + reason));
                else
                {
                    committingScan = true;
                    Control<TextBlock>("ScanStage").Text = "正在载入新的扫描结果…";
                    SetControls();
                    await PublishAsync(run.Output);
                    stale = false;
                    try { File.Delete(StaleMarker); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    SetStatus("刷新完成。已载入 " + roots.Count + " 个扫描根目录，按占用空间降序显示。");
                }
            }
            catch (Exception ex) { SetStatus("更新快照失败，点击刷新重试。  " + ex.GetBaseException().Message); }
            finally
            {
                scan = null;
                committingScan = false;
                scanClock.Stop();
                scanTime.Stop();
                run.Dispose();
                if (db == null) SetEmpty("扫描未完成", "点击「刷新」重新扫描磁盘。");
                if (db == null) Control<TextBlock>("Stamp").Text = "尚未完成扫描";
                SetControls();
                if (closing) window.Close();
            }
        }

        private async Task PublishAsync(string staged)
        {
            var incoming = await Task.Run(delegate
            {
                using (var candidate = new Database(staged))
                {
                    bool freshIds;
                    var items = ReadRoots(candidate, out freshIds);
                    if (!freshIds) throw new InvalidDataException("扫描结果缺少文件标识。");
                    candidate.Query("SELECT id,parent,name,bytes,logical_bytes,flags,fsid FROM entries LIMIT 0", delegate {});
                    candidate.Query("SELECT id,parent,name,bytes,logical_bytes,flags,fsid,files FROM dirs LIMIT 0", delegate {});
                    var root = items[0];
                    AppendPage(root, ReadPage(candidate, root, freshIds));
                    return items;
                }
            });
            string previous = activePath;
            bool replaced = false;
            string backup = null;
            var oldDatabase = db;
            db = null;
            bool rebinding = false;
            Exception failure;
            try
            {
                db = await Task.Run(delegate
                {
                    if (oldDatabase != null) oldDatabase.Dispose();
                    if (File.Exists(Snapshot))
                    {
                        backup = staged + ".previous";
                        File.Replace(staged, Snapshot, backup, true);
                    }
                    else File.Move(staged, Snapshot);
                    replaced = true;
                    return new Database(Snapshot);
                });
                activePath = Snapshot;
                hasIdentities = true;
                rebinding = true;
                Display(incoming);
                // The worker cleans its staged backup after the new tree is on screen.
                return;
            }
            catch (Exception ex) { failure = ex; }

            if (db != null) { db.Dispose(); db = null; }
            db = await Task.Run(delegate
            {
                if (replaced && backup != null) File.Replace(backup, Snapshot, null, true);
                return previous == null ? null : new Database(previous);
            });
            activePath = previous;
            if (rebinding && db != null)
            {
                bool ids;
                var previousRoots = ReadRoots(db, out ids);
                hasIdentities = ids;
                Display(previousRoots);
            }
            throw new IOException("无法切换磁盘快照。", failure);
        }

        internal void TogglePick(Node item)
        {
            if (!AllowPick) return;
            if (item.ExplicitPick) { item.ExplicitPick = false; picked.Remove(item); }
            else
            {
                if (item.IsDirectory)
                {
                    foreach (var prior in picked.Where(n => DescendsFrom(n, item)).ToArray())
                    {
                        prior.ExplicitPick = false;
                        picked.Remove(prior);
                    }
                }
                item.ExplicitPick = true;
                picked.Add(item);
            }
            item.UpdateSelection();
            SetControls();
        }
        private static bool DescendsFrom(Node child, Node ancestor)
        {
            for (Node item = child.ParentNode; item != null; item = item.ParentNode) if (item == ancestor) return true;
            return false;
        }
        private void ClearPicks()
        {
            foreach (var item in picked) item.ExplicitPick = false;
            picked.Clear();
            UpdatePicks();
        }

        private bool PathDialog(string title, string text, bool confirm)
        {
            int lines = 1;
            foreach (char c in text) if (c == '\n') lines++;
            var dialog = new Window
            {
                Title = title,
                Owner = window,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = Math.Min(780, SystemParameters.WorkArea.Width * .9),
                Height = Math.Min(560, Math.Max(340, 280 + Math.Min(12, lines) * 18)),
                MinWidth = 520,
                MinHeight = 320,
                FontFamily = window.FontFamily,
                FontSize = 13,
                Background = Design.Brush("CanvasBrush"),
                Foreground = Design.Brush("InkBrush"),
                ShowInTaskbar = false,
                UseLayoutRounding = true
            };
            AutomationProperties.SetAutomationId(dialog, confirm ? "RecycleDialog" : "ReportDialog");

            var card = new Border { Style = Design.Style("SurfaceCard"), Margin = new Thickness(18), Padding = new Thickness(24, 22, 24, 18) };
            var box = new DockPanel { LastChildFill = true };
            card.Child = box;
            dialog.Content = card;

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            header.Children.Add(UiKit.IconTile(confirm ? "IconWarning" : "IconCheck", 44,
                Design.Brush(confirm ? "WarningSoftBrush" : "SuccessSoftBrush"),
                Design.Brush(confirm ? "WarningBrush" : "SuccessBrush")));
            var heading = new StackPanel { Margin = new Thickness(13, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(new TextBlock
            {
                Text = confirm ? "确认移入回收站" : "回收站处理结果",
                FontFamily = Design.Font("DisplayFont"),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold
            });
            heading.Children.Add(new TextBlock
            {
                Text = confirm
                    ? "所选 " + picked.Count.ToString("N0") + " 项将移入 Windows 回收站，可随时还原。"
                    : "以下是每一项的处理结果，成功与失败的条目都会列出。",
                Style = Design.Style("Subtitle"),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            header.Children.Add(heading);
            DockPanel.SetDock(header, Dock.Top);
            box.Children.Add(header);

            if (confirm)
            {
                var note = new Border
                {
                    Background = Design.Brush("WarningSoftBrush"),
                    BorderBrush = Design.Brush("WarningBorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 14, 0, 0),
                    Child = new TextBlock
                    {
                        Text = "目录会连同内部全部内容一起移入回收站。",
                        FontSize = 12,
                        Foreground = Design.Brush("WarningTextBrush"),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                DockPanel.SetDock(note, Dock.Top);
                box.Children.Add(note);
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            box.Children.Add(buttons);
            var cancel = UiKit.Button(confirm ? "取消" : "关闭", null, confirm ? "BaseButton" : "BaseButton", confirm ? "CancelRecycle" : "CloseReport");
            cancel.IsCancel = true;
            cancel.IsDefault = !confirm;
            cancel.Click += delegate { dialog.DialogResult = false; };
            buttons.Children.Add(cancel);
            if (confirm)
            {
                var yes = UiKit.Button("确认移入回收站", "IconTrash", "DangerButton", "ConfirmRecycle");
                yes.IsDefault = true;
                yes.Margin = new Thickness(4, 0, 0, 0);
                yes.Click += delegate { dialog.DialogResult = true; };
                buttons.Children.Add(yes);
            }

            var frame = new Border
            {
                Background = Design.Brush("SurfaceBrush"),
                BorderBrush = Design.Brush("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 16, 0, 0),
                Padding = new Thickness(12, 8, 8, 8)
            };
            var paths = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = Design.Font("MonoFont"),
                FontSize = 12,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0)
            };
            AutomationProperties.SetAutomationId(paths, confirm ? "ConfirmPaths" : "ResultPaths");
            frame.Child = paths;
            box.Children.Add(frame);

            dialog.Loaded += delegate { cancel.Focus(); };
            return dialog.ShowDialog() == true;
        }

        private void BeginRecycle()
        {
            if (!AllowPick || picked.Count == 0 || closing) return;
            var list = picked.OrderBy(n => n.FullPath, StringComparer.Ordinal).ToList();
            if (!PathDialog("确认移入回收站", String.Join(Environment.NewLine, list.Select(n => n.FullPath).ToArray()), true)) return;
            try { File.WriteAllText(StaleMarker, DateTimeOffset.Now.ToString("o")); }
            catch (Exception ex) { SetStatus("无法记录待刷新状态。  " + ex.GetBaseException().Message); return; }
            var targets = list.Select(n => new RecycleTarget { Path = n.FullPath, IsDirectory = n.IsDirectory, FileId = n.FileId, VolumeSerial = n.Serial.Value }).ToList();
            var hwnd = new WindowInteropHelper(window).Handle;
            recycling = true;
            stale = true;
            SetStatus("正在将 " + targets.Count + " 项移入回收站…");
            UpdatePicks();
            var worker = new Thread(delegate()
            {
                List<RecycleResult> results = null;
                string error = null;
                try { results = Recycler.Recycle(targets, hwnd); }
                catch (Exception ex) { error = ex.GetBaseException().Message; }
                window.Dispatcher.BeginInvoke(new Action(delegate { EndRecycle(list, results, error); }));
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
        private void EndRecycle(List<Node> items, List<RecycleResult> results, string error)
        {
            recycling = false;
            var report = new List<string>();
            int success = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var result = results != null && i < results.Count ? results[i] : null;
                bool ok = result != null && result.Success;
                if (ok) { item.Recycled = true; success++; }
                string detail = result == null ? error ?? "未取得操作结果" : result.Message;
                report.Add((ok ? "成功" : "失败") + "  " + item.FullPath + Environment.NewLine + "     " + detail);
            }
            lastReport = String.Join(Environment.NewLine + Environment.NewLine, report.ToArray());
            Control<TextBlock>("RecycleSummary").Text = "成功 " + success + " 项，失败 " + (items.Count - success) + " 项";
            Control<Button>("RecycleReport").Visibility = Visibility.Visible;
            ClearPicks();
            if (closing) window.Close(); else BeginRefresh();
        }

        private void OnClosing(object sender, CancelEventArgs args)
        {
            if (updateDialog != null && updateDialog.IsBusy)
            {
                args.Cancel = true;
                closing = true;
                updateDialog.RequestClose();
                if (scan != null) CancelRefresh();
                return;
            }
            if (scan == null && !recycling) return;
            args.Cancel = true;
            closing = true;
            if (scan != null) CancelRefresh();
            else SetStatus("正在完成回收站操作，结束后将自动关闭。");
        }
        public void Dispose()
        {
            disposed = true;
            if (scan != null) scan.Dispose();
            if (db != null) db.Dispose();
        }
    }
}
