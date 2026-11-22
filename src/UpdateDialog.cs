using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace UsageTree
{
    internal sealed partial class Viewer
    {
        private UpdateDialog updateDialog;
        private Mutex updateGate;
        private bool updateInProgress;
        private string currentVersion;
        private string PendingUpdate { get { return Path.Combine(dataDirectory, "update-pending.version"); } }
        internal string UpdateLog { get { return Path.Combine(dataDirectory, "update-install.log"); } }
        internal string CurrentVersion { get { return currentVersion; } }
        internal Version InstalledVersion { get; private set; }

        private void AttachUpdates()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyInformationalVersionAttribute));
            currentVersion = info == null ? assembly.GetName().Version.ToString(3) : info.InformationalVersion;
            Version parsed;
            InstalledVersion = Version.TryParse(currentVersion.Split('+','-')[0], out parsed) ? parsed : assembly.GetName().Version;
            Control<TextBlock>("AppVersion").Text = "v" + currentVersion;
            ShowPreviousUpdate();
            ThreadPool.QueueUserWorkItem(delegate {
                try { UpdateService.CleanStaleDownloads(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            });
            Control<Button>("CheckUpdates").Click += delegate
            {
                if (closing) return;
                if (updateDialog != null)
                {
                    if (updateDialog.WindowState == WindowState.Minimized) updateDialog.WindowState = WindowState.Normal;
                    updateDialog.Activate();
                    return;
                }
                var dialog = new UpdateDialog(this, window);
                updateDialog = dialog;
                dialog.Closed += delegate
                {
                    updateDialog = null;
                    if (closing && !disposed) window.Dispatcher.BeginInvoke(new Action(delegate { if (window.IsVisible) window.Close(); }));
                };
                dialog.Show();
            };
        }

        private void ShowPreviousUpdate()
        {
            try
            {
                if (!File.Exists(PendingUpdate) || new FileInfo(PendingUpdate).Length > 256) return;
                Version target;
                if (!Version.TryParse(File.ReadAllText(PendingUpdate).Trim(), out target)) return;
                var notice = Control<TextBlock>("UpdateNotice");
                bool reached = CompareVersions(InstalledVersion, target) >= 0;
                notice.Text = reached ? "已完成更新，当前版本 v" + currentVersion + "。" :
                    "上次安装的目标版本为 v" + target + "，当前仍为 v" + currentVersion + "。可在「检查更新」中重试或查看安装日志。";
                notice.Foreground = reached ? Brushes.Teal : Brushes.DarkGoldenrod;
                notice.Visibility = Visibility.Visible;
                if (reached) File.Delete(PendingUpdate);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal string UpdateBlockReason
        {
            get
            {
                if (closing || disposed) return "应用正在关闭。";
                if (recycling) return "回收站操作完成后即可安装更新。";
                if (scan != null) return "扫描结束后即可安装更新，也可以在主窗口取消扫描。";
                return "";
            }
        }
        internal int PickCount { get { return picked.Count; } }
        internal static int CompareVersions(Version left, Version right)
        {
            return new Version(left.Major,left.Minor,Math.Max(0,left.Build),Math.Max(0,left.Revision)).CompareTo(
                new Version(right.Major,right.Minor,Math.Max(0,right.Build),Math.Max(0,right.Revision)));
        }

        internal void BeginUpdateTransfer()
        {
            string reason = UpdateBlockReason;
            if (reason.Length != 0) throw new InvalidOperationException(reason);
            if (updateInProgress) throw new InvalidOperationException("本次更新已开始。");
            using (var identity = WindowsIdentity.GetCurrent())
                updateGate = new Mutex(false, @"Local\WindowsDiskManager-Update-" + identity.User.Value);
            bool acquired;
            try { acquired = updateGate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                updateGate.Dispose();
                updateGate = null;
                throw new InvalidOperationException("另一个磁盘空间管理器窗口正在更新，请稍后重试。");
            }
            updateInProgress = true;
            UpdatePicks();
        }
        internal void EndUpdateTransfer()
        {
            if (updateGate != null)
            {
                updateGate.ReleaseMutex();
                updateGate.Dispose();
                updateGate = null;
            }
            updateInProgress = false;
            UpdatePicks();
        }
        internal void LaunchVerifiedSetup(UpdateRelease release, string path)
        {
            if (!updateInProgress || UpdateBlockReason.Length != 0)
                throw new InvalidOperationException("当前操作尚未结束，请稍后重试更新。");
            File.WriteAllText(PendingUpdate, release.Version.ToString());
            string args;
            using (var process = Process.GetCurrentProcess())
                args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATEPID=" + process.Id.ToString(CultureInfo.InvariantCulture) +
                    " /RELAUNCH /LOG=\"" + UpdateLog + "\"";
            UpdateService.StartInstaller(release, path, args);
        }
        internal void CloseForUpdate()
        {
            closing = true;
            window.Close();
        }
    }

    internal sealed class UpdateDialog : Window
    {
        private readonly Viewer manager;
        private readonly TextBlock latest, state, activityNote;
        private readonly ProgressBar bar;
        private readonly Button recheck, install, cancel, close, showLog;
        private UpdateRelease release;
        private CancellationTokenSource work;
        private bool downloading, closeWhenDone, committed;
        internal bool IsBusy { get { return work != null; } }

        internal UpdateDialog(Viewer manager, Window owner)
        {
            this.manager = manager;
            Title = "软件更新";
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = owner.FontFamily;
            FontSize = 13;
            Background = new SolidColorBrush(Color.FromRgb(245,248,249));
            Foreground = new SolidColorBrush(Color.FromRgb(32,51,69));
            Width = Math.Min(558, SystemParameters.WorkArea.Width * .95);
            Height = Math.Min(360, SystemParameters.WorkArea.Height * .9);
            MinWidth = 420;
            MinHeight = 315;
            AutomationProperties.SetAutomationId(this, "UpdateWindow");
            var layout = new DockPanel { Margin = new Thickness(22) };
            Content = layout;
            var headline = new TextBlock { Text = Title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,12) };
            DockPanel.SetDock(headline, Dock.Top); layout.Children.Add(headline);
            var versions = new Grid { Margin = new Thickness(0,0,0,12) };
            versions.ColumnDefinitions.Add(new ColumnDefinition());
            versions.ColumnDefinitions.Add(new ColumnDefinition());
            var current = VersionCell(versions, 0, "当前版本", "CurrentVersion");
            current.Text = "v" + manager.CurrentVersion;
            latest = VersionCell(versions, 1, "最新稳定版本", "LatestVersion");
            latest.Text = "待检查";
            DockPanel.SetDock(versions, Dock.Top); layout.Children.Add(versions);
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,10,0,0) };
            DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
            showLog = ActionButton(buttons, "查看安装日志", "OpenUpdateLog");
            recheck = ActionButton(buttons, "重新检查", "RecheckUpdate");
            install = ActionButton(buttons, "下载并自动安装", "InstallUpdate");
            cancel = ActionButton(buttons, "取消", "CancelUpdate");
            close = ActionButton(buttons, "关闭", "CloseUpdate");
            var messages = new StackPanel();
            var scroll = new ScrollViewer { Content = messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            layout.Children.Add(scroll);
            state = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(state, "UpdateStatus");
            messages.Children.Add(state);
            bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0,10,4,0), Foreground = Brushes.Teal };
            AutomationProperties.SetAutomationId(bar, "UpdateProgress");
            messages.Children.Add(bar);
            activityNote = new TextBlock { Margin = new Thickness(0,8,0,0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray };
            AutomationProperties.SetAutomationId(activityNote, "UpdateActivityNote");
            messages.Children.Add(activityNote);
            recheck.Click += async delegate { await Check(); };
            install.Click += async delegate { await DownloadAndInstall(); };
            cancel.Click += delegate { CancelWork(); };
            close.Click += delegate { RequestClose(); };
            showLog.Click += delegate { OpenLog(); };
            Loaded += async delegate { await Check(); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args) {
                if (IsBusy && !committed) { args.Cancel = true; RequestClose(); }
            };
            RefreshActions();
        }

        private static TextBlock VersionCell(Grid grid, int column, string label, string id)
        {
            var cells = new StackPanel();
            cells.Children.Add(new TextBlock { Text = label, Foreground = Brushes.SlateGray, FontSize = 12 });
            var number = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold };
            AutomationProperties.SetAutomationId(number, id);
            cells.Children.Add(number);
            var border = new Border { Child = cells, Background = Brushes.White, Padding = new Thickness(12,8,12,8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(215,226,230)), BorderThickness = new Thickness(1),
                Margin = column == 0 ? new Thickness(0,0,4,0) : new Thickness(4,0,0,0) };
            Grid.SetColumn(border, column); grid.Children.Add(border);
            return number;
        }
        private static Button ActionButton(Panel parent, string label, string id)
        {
            var button = new Button { Content = label, Padding = new Thickness(9,5,9,5), Margin = new Thickness(5,2,0,2),
                Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(169,190,196)), MinHeight = 28 };
            AutomationProperties.SetAutomationId(button, id);
            parent.Children.Add(button);
            return button;
        }
        internal void RefreshActions()
        {
            bool eligible = release != null && release.IsNewer;
            string blocked = manager.UpdateBlockReason;
            recheck.IsEnabled = !IsBusy && !committed;
            install.Visibility = eligible && !downloading ? Visibility.Visible : Visibility.Collapsed;
            install.IsEnabled = !IsBusy && !committed && blocked.Length == 0;
            cancel.Visibility = IsBusy && !committed ? Visibility.Visible : Visibility.Collapsed;
            cancel.IsEnabled = IsBusy && !work.IsCancellationRequested;
            close.IsEnabled = !committed;
            showLog.Visibility = File.Exists(manager.UpdateLog) ? Visibility.Visible : Visibility.Collapsed;
            bar.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
            activityNote.Text = eligible && !downloading ? blocked.Length != 0 ? blocked :
                "安装完成后将重新打开应用。" + (manager.PickCount == 0 ? "" : "当前勾选的 " + manager.PickCount + " 项将在重启后清空。") : "";
        }
        private void CancelWork()
        {
            if (!IsBusy || committed || work.IsCancellationRequested) return;
            state.Text = "正在取消…";
            work.Cancel();
            RefreshActions();
        }
        internal void RequestClose()
        {
            if (!IsBusy) { Close(); return; }
            closeWhenDone = true;
            CancelWork();
        }

        private async Task Check()
        {
            if (IsBusy || committed) return;
            release = null;
            work = new CancellationTokenSource();
            var token = work.Token;
            state.Text = "正在检查 GitHub 上的最新稳定版本…";
            latest.Text = "检查中";
            bar.IsIndeterminate = true;
            RefreshActions();
            try
            {
                var found = await UpdateService.CheckAsync(token);
                token.ThrowIfCancellationRequested();
                release = found;
                latest.Text = "v" + found.VersionText.TrimStart('v','V');
                if (found.IsNewer) state.Text = "有新版本可用，安装包大小为 " + Node.FormatSize(found.Size) + "。";
                else if (Viewer.CompareVersions(manager.InstalledVersion, found.Version) > 0)
                    state.Text = "当前版本高于已发布的稳定版本。";
                else state.Text = "当前已是最新稳定版本。";
            }
            catch (OperationCanceledException) { latest.Text = "未完成"; state.Text = token.IsCancellationRequested ? "已取消检查。" : "检查超时，请重试并确认系统代理可以连接 GitHub。"; }
            catch (Exception ex) { latest.Text = "未取得"; state.Text = ErrorText("检查更新失败", ex); }
            finally { FinishWork(); }
        }

        private async Task DownloadAndInstall()
        {
            if (IsBusy || committed || release == null || !release.IsNewer || manager.UpdateBlockReason.Length != 0) return;
            try { manager.BeginUpdateTransfer(); }
            catch (Exception ex) { state.Text = ErrorText("暂时无法安装", ex); return; }
            var selected = release;
            var operation = new CancellationTokenSource();
            work = operation;
            downloading = true;
            state.Text = "正在下载 " + selected.AssetName + "  ·  0%";
            bar.IsIndeterminate = false;
            bar.Value = 0;
            RefreshActions();
            string file = null;
            try
            {
                var progress = new Progress<int>(delegate(int percent)
                {
                    if (work != operation || operation.IsCancellationRequested || committed) return;
                    bar.Value = Math.Max(0, Math.Min(100, percent));
                    state.Text = percent >= 100 ? "下载完成，正在校验安装包…" : "正在下载 " + selected.AssetName + "  ·  " + (int)bar.Value + "%";
                });
                file = await UpdateService.DownloadAsync(selected, progress, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                state.Text = "正在启动已校验的安装程序…";
                manager.LaunchVerifiedSetup(selected, file);
                committed = true;
            }
            catch (OperationCanceledException) { state.Text = operation.IsCancellationRequested ? "下载已取消，可以重新下载。" : "下载超时，请重试并确认系统代理可以连接 GitHub。"; }
            catch (Exception ex) { state.Text = ErrorText("更新失败", ex); }
            finally
            {
                if (!committed && file != null)
                {
                    try { UpdateService.DiscardInstaller(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                downloading = false;
                manager.EndUpdateTransfer();
                FinishWork();
            }
            if (committed) manager.CloseForUpdate();
        }
        private void FinishWork()
        {
            work.Dispose();
            work = null;
            RefreshActions();
            if (closeWhenDone && !committed) Close();
        }
        private void OpenLog()
        {
            try
            {
                var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + manager.UpdateLog + "\"") { UseShellExecute = false };
                using (Process.Start(info)) { }
            }
            catch (Exception ex) { state.Text = ErrorText("无法打开安装日志", ex); }
        }
        private static string ErrorText(string action, Exception ex)
        {
            string message = action + "。  " + ex.Message;
            if (ex.GetBaseException().Message != ex.Message) message += "  " + ex.GetBaseException().Message;
            for (Exception cause = ex; cause != null; cause = cause.InnerException)
            {
                if (cause is WebException || cause is TimeoutException || cause is System.Net.Sockets.SocketException ||
                    cause.GetType().FullName == "System.Net.Http.HttpRequestException")
                    return message + "  请检查网络和系统代理后重试。";
            }
            return message;
        }
    }
}
