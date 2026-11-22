using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
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
        private readonly Dictionary<string,string> counters = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        internal bool AllowPick { get { return !recycling && !updateInProgress && scan == null && hasIdentities; } }

        private T Control<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
        private void AttachControls()
        {
            AttachUpdates();
            Control<Button>("Refresh").Click += delegate { BeginRefresh(); };
            Control<Button>("CancelScan").Click += delegate { CancelRefresh(); };
            Control<Button>("ClearSelection").Click += delegate { ClearPicks(); };
            Control<Button>("RecycleSelection").Click += delegate { BeginRecycle(); };
            Control<Button>("RecycleReport").Click += delegate { PathDialog("回收站处理结果", lastReport, false); };
            window.PreviewKeyDown += delegate(object sender, KeyEventArgs args) {
                if (args.Key == Key.F5) { BeginRefresh(); args.Handled = true; }
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
        private void SetControls()
        {
            bool busy = scan != null || recycling || updateInProgress;
            Control<Button>("Refresh").IsEnabled = !busy;
            var cancel = Control<Button>("CancelScan");
            cancel.Visibility = scan == null ? Visibility.Collapsed : Visibility.Visible;
            cancel.IsEnabled = scan != null && !scan.Cancelled;
            Control<Button>("ClearSelection").IsEnabled = picked.Count != 0 && !busy;
            Control<Button>("RecycleSelection").IsEnabled = picked.Count != 0 && AllowPick;
            Control<TextBlock>("SelectionCount").Text = "已选 " + picked.Count.ToString("N0") + " 项";
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
            ScanRun run;
            try { run = new ScanRun(dataDirectory); }
            catch (Exception ex) { SetStatus("无法准备扫描目录，点击刷新重试。  " + ex.GetBaseException().Message); return; }
            scan = run;
            SetStatus(db == null ? "正在准备首次扫描…" : "正在重新扫描，可继续浏览当前快照。");
            UpdatePicks();
            try
            {
                run.Start(
                    delegate(string line) { window.Dispatcher.BeginInvoke(new Action(delegate { ShowProgress(run, line); })); },
                    delegate(int code, string detail) { window.Dispatcher.BeginInvoke(new Action(delegate { EndRefresh(run, code, detail); })); });
            }
            catch (Exception ex) { EndRefresh(run, 1, ex.GetBaseException().Message); }
        }
        private void ShowProgress(ScanRun run, string line)
        {
            if (scan != run || run.Cancelled || disposed) return;
            string[] words = line.Split('\t');
            if (words[0] == "SCAN" && words.Length >= 4)
            {
                long dirs, files;
                if (!Int64.TryParse(words[2], out dirs) || !Int64.TryParse(words[3], out files)) return;
                counters[words[1]] = words[1] + "  " + dirs.ToString("N0") + " 个目录 / " + files.ToString("N0") + " 个文件";
                SetStatus("扫描中  " + String.Join("     ·     ", counters.Values.ToArray()));
            }
            else if (words[0] == "STAGE" && words.Length >= 2) SetStatus(words[1]);
        }
        private void CancelRefresh()
        {
            if (scan == null) return;
            scan.Cancel();
            SetStatus("正在取消扫描…");
            SetControls();
        }
        private void EndRefresh(ScanRun run, int code, string reason)
        {
            if (scan != run || disposed) return;
            try
            {
                if (run.Cancelled || code == 2) SetStatus("已取消扫描。点击刷新可重新开始。");
                else if (code != 0) SetStatus("扫描失败，点击刷新重试。" + (String.IsNullOrWhiteSpace(reason) ? "" : "  " + reason));
                else
                {
                    Publish(run.Output);
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
                run.Dispose();
                UpdatePicks();
                if (closing) window.Close();
            }
        }

        private void Publish(string staged)
        {
            bool freshIds;
            List<Node> incoming;
            using (var candidate = new Database(staged))
            {
                incoming = ReadRoots(candidate, out freshIds);
                if (!freshIds) throw new InvalidDataException("扫描结果缺少文件标识。");
                candidate.Query("SELECT id,parent,name,bytes,logical_bytes,flags,fsid FROM entries LIMIT 0", delegate {});
                candidate.Query("SELECT id,parent,name,bytes,logical_bytes,flags,fsid,files FROM dirs LIMIT 0", delegate {});
            }
            string previous = activePath;
            bool replaced = false;
            string backup = null;
            if (db != null) { db.Dispose(); db = null; }
            try
            {
                if (File.Exists(Snapshot))
                {
                    backup = staged + ".previous";
                    File.Replace(staged, Snapshot, backup, true);
                }
                else File.Move(staged, Snapshot);
                replaced = true;
                db = new Database(Snapshot);
                activePath = Snapshot;
                hasIdentities = freshIds;
                Display(incoming);
            }
            catch
            {
                if (db != null) { db.Dispose(); db = null; }
                if (replaced && backup != null && File.Exists(backup))
                {
                    File.Replace(backup, Snapshot, null, true);
                    backup = null;
                }
                if (previous != null && File.Exists(previous)) OpenExisting(previous);
                throw;
            }
            if (backup != null)
            {
                try { File.Delete(backup); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal void TogglePick(Node item)
        {
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
            var dialog = new Window { Title = title, Owner = window, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = Math.Min(760, SystemParameters.WorkArea.Width * .9), Height = Math.Min(440, SystemParameters.WorkArea.Height * .85),
                MinWidth = 450, MinHeight = 240, FontFamily = window.FontFamily, Background = Brushes.White, ShowInTaskbar = false };
            var box = new DockPanel { Margin = new Thickness(18) };
            var headline = new TextBlock { Text = confirm ? "将所选 " + picked.Count.ToString("N0") + " 项移入回收站？" : "逐项处理结果", FontWeight = FontWeights.SemiBold, FontSize = 18, Margin = new Thickness(0,0,0,6) };
            DockPanel.SetDock(headline, Dock.Top); box.Children.Add(headline);
            if (confirm)
            {
                var note = new TextBlock { Text = "目录将连同内部全部内容一起移入回收站。", Margin = new Thickness(0,0,0,10) };
                DockPanel.SetDock(note, Dock.Top); box.Children.Add(note);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
            DockPanel.SetDock(buttons, Dock.Bottom); box.Children.Add(buttons);
            var cancel = new Button { Content = confirm ? "取消" : "关闭", IsCancel = true, IsDefault = !confirm, Padding = new Thickness(16,5,16,5) };
            AutomationProperties.SetAutomationId(cancel, confirm ? "CancelRecycle" : "CloseReport");
            cancel.Click += delegate { dialog.DialogResult = false; };
            buttons.Children.Add(cancel);
            if (confirm)
            {
                var yes = new Button { Content = "确认移入回收站", Padding = new Thickness(16,5,16,5), Margin = new Thickness(8,0,0,0) };
                AutomationProperties.SetAutomationId(yes, "ConfirmRecycle");
                yes.Click += delegate { dialog.DialogResult = true; };
                buttons.Children.Add(yes);
            }
            var paths = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8), Background = new SolidColorBrush(Color.FromRgb(247,249,250)) };
            AutomationProperties.SetAutomationId(paths, confirm ? "ConfirmPaths" : "ResultPaths");
            box.Children.Add(paths);
            dialog.Content = box;
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
