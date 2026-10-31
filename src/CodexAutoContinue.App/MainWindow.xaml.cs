using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexAutoContinue.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CodexAutoContinue.App;

public partial class MainWindow : Window
{
    private readonly RunController _controller;
    private readonly StateStore _store;
    private readonly RotatingLog _log;
    private readonly Forms.NotifyIcon _tray;
    private readonly DispatcherTimer _timer;
    private readonly bool _smoke;
    private readonly bool _startInTray;
    private bool _exit, _busy, _tickBusy, _ready;
    private bool _smokeFailed;
    private string _version="";

    public MainWindow(string dataFolder, bool smoke=false, bool startInTray=false)
    {
        _smoke=smoke; _startInTray=startInTray;
        InitializeComponent();
        _store=new(dataFolder); _log=new(dataFolder);
        _controller=new(new AppServerTransport(),_store,new FileRunLease(Path.Combine(dataFolder,"locks")));
        _controller.Changed += ()=>Dispatcher.BeginInvoke(Refresh);
        _controller.Log += message=>Dispatcher.BeginInvoke(()=>WriteJournal(message));
        _controller.Output += message=>Dispatcher.BeginInvoke(()=>Append(OutputBox,message+Environment.NewLine+Environment.NewLine));
        var s=_controller.Data.Settings;
        CliPathBox.Text=string.IsNullOrWhiteSpace(s.CliPath) ? FindCli() ?? "" : s.CliPath;
        ContinueBox.Text=s.ContinueMessage; PollBox.Text=s.PollSeconds.ToString();
        StartupBox.IsChecked=s.StartWithWindows; RestoreBox.IsChecked=s.RestoreWaiting; NotificationsBox.IsChecked=s.Notifications; TrayCloseBox.IsChecked=s.CloseToTray;
        ProjectBox.Text=_controller.Data.Run.ProjectPath; PromptBox.Text=_controller.Data.Run.OriginalPrompt; SessionIdBox.Text=_controller.Data.Run.ThreadId ?? "";
        AutoBox.IsChecked=_controller.Data.Run.ThreadId is null || _controller.Data.Run.AutoContinue;
        DataFolderText.Text="Данные и журнал: "+dataFolder;
        var iconUri=new Uri("pack://application:,,,/Assets/app.ico");
        using var iconStream=Application.GetResourceStream(iconUri)!.Stream;
        _tray=new Forms.NotifyIcon { Icon=new System.Drawing.Icon(iconStream), Text="Codex AutoContinue", Visible=true };
        _controller.Notice += (title,text)=>Dispatcher.BeginInvoke(()=> { if (_controller.Data.Settings.Notifications && !_exit) _tray.ShowBalloonTip(5000,title,text.Length>250 ? text[..250] : text,Forms.ToolTipIcon.Info); });
        Icon=BitmapFrame.Create(iconUri);
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть окно",null,(_,_)=>Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Приостановить",null,(_,_)=>Dispatcher.Invoke(()=>_ = RunAction(_controller.PauseAsync)));
        menu.Items.Add("Продолжить",null,(_,_)=>Dispatcher.Invoke(()=>_ = RunAction(async ()=> { await SaveSettings(); await _controller.ContinueNowAsync(); })));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Полный выход",null,(_,_)=>Dispatcher.Invoke(()=>_ = ExitAsync()));
        _tray.ContextMenuStrip=menu; _tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(ShowWindow);
        _timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(1) };
        _timer.Tick+=async (_,_)=>
        {
            RefreshCountdown();
            if (_tickBusy || _busy || _smoke) return;
            _tickBusy=true;
            try { await _controller.TickAsync(); } catch (Exception e) { WriteJournal("Фоновая проверка: "+e.Message); }
            finally { _tickBusy=false; }
        };
        SystemEvents.PowerModeChanged+=PowerModeChanged;
        _ready=true; Refresh();
        if (_store.LoadWarning is { } warning) WriteJournal(warning);
    }

    private async void Window_Loaded(object sender,RoutedEventArgs e)
    {
        _timer.Start();
        if (_startInTray && !_smoke) Hide();
        if (_smoke) { await SmokeTestAsync(); return; }
        if (!string.IsNullOrWhiteSpace(CliPathBox.Text)) await RunAction(Connect);
        else WriteJournal("CLI не найден. Откройте настройки: npm install -g @openai/codex, затем codex login. Выберите нативный codex.exe.");
    }
    private static string? FindCli()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var p=Path.Combine(dir.Trim('"'),"codex.exe"); if (File.Exists(p)) return Path.GetFullPath(p); } catch (ArgumentException) { }
        }
        var user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extensions=Path.Combine(user,".vscode","extensions");
        if (Directory.Exists(extensions))
        {
            foreach (var d in Directory.GetDirectories(extensions,"openai.chatgpt-*").OrderByDescending(Directory.GetLastWriteTimeUtc))
            { var p=Path.Combine(d,"bin","windows-x86_64","codex.exe"); if (File.Exists(p)) return p; }
        }
        // npm's .cmd shim is avoided; run the package's native executable directly.
        var npm=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"npm","node_modules","@openai");
        if (Directory.Exists(npm))
            return Directory.EnumerateFiles(npm,"codex.exe",SearchOption.AllDirectories).FirstOrDefault(p=>p.Contains("x86_64",StringComparison.OrdinalIgnoreCase));
        return null;
    }
    private async Task Connect()
    {
        var path=CliPathBox.Text.Trim().Trim('"');
        using var proc=Process.Start(AppServerTransport.ProcessInfo(path,"--version")) ?? throw new IOException("Не удалось проверить CLI.");
        var stdout=proc.StandardOutput.ReadToEndAsync(); var stderr=proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        _version=(await stdout).Trim(); await stderr;
        await _controller.ConnectAsync(path); WriteJournal("Версия CLI: "+_version);
    }
    private async Task SaveSettings()
    {
        if (!int.TryParse(PollBox.Text,out var poll) || poll <30 || poll >900) throw new InvalidOperationException("Интервал проверки должен быть целым числом от 30 до 900 секунд.");
        if (string.IsNullOrWhiteSpace(ContinueBox.Text)) throw new InvalidOperationException("Введите сообщение продолжения.");
        var s=_controller.Data.Settings;
        var startup=StartupBox.IsChecked == true;
        if (startup != s.StartWithWindows) SetStartup(startup);
        s.StartWithWindows=startup; s.ContinueMessage=ContinueBox.Text; s.PollSeconds=poll;
        s.RestoreWaiting=RestoreBox.IsChecked == true; s.Notifications=NotificationsBox.IsChecked == true; s.CloseToTray=TrayCloseBox.IsChecked == true;
        // A running connection keeps its executable until reconnect; this path is used on next launch.
        s.CliPath=CliPathBox.Text.Trim().Trim('"');
        await _controller.SaveSettingsAsync();
    }
    private static void SetStartup(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var path=Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь приложения.");
            key.SetValue("CodexAutoContinue","\""+path+"\" --tray",RegistryValueKind.String);
        }
        else key.DeleteValue("CodexAutoContinue",false);
    }
    private async Task RunAction(Func<Task> action)
    {
        if (_busy) return;
        _busy=true; StartButton.IsEnabled=false;
        try { await action(); }
        catch (Exception e) { WriteJournal("Ошибка действия: "+Redactor.Clean(e.Message)); MessageBox.Show(this,Redactor.Clean(e.Message),"Codex AutoContinue",MessageBoxButton.OK,MessageBoxImage.Information); }
        finally { _busy=false; StartButton.IsEnabled=true; Refresh(); }
    }
    private void Refresh()
    {
        if (!_ready || _exit) return;
        var r=_controller.Data.Run;
        StateText.Text=RunController.StateLabel(r.State); DetailText.Text=r.Detail;
        CurrentTaskText.Text=string.IsNullOrWhiteSpace(r.OriginalPrompt) ? "Текущая задача не выбрана" : r.OriginalPrompt.Replace('\n',' ');
        CurrentIdText.Text="ID: "+(r.ThreadId ?? "—");
        CliStatusText.Text=_controller.CliStatus+(_version.Length>0 ? " · "+_version : ""); AuthText.Text=_controller.AuthStatus;
        AutoStatusText.Text=r.Owned && r.AutoContinue && !r.Paused && !r.UserStopped ? "Автопродолжение включено" : "Автопродолжение не активно";
        if (_controller.Limits is { } limits)
        { LimitsItems.ItemsSource=limits.Windows; LimitInfoText.Text=limits.Evaluate().Reason; }
        var req=_controller.PendingRequests;
        ApprovalButton.IsEnabled=req.Count>0;
        ApprovalSummary.Text=req.Count>0 ? $"Ожидают ответа: {req.Count}. "+ApprovalDialog.RequestTitle(req[0].Method) : "Нет запросов разрешений.";
        _tray.Text="Codex AutoContinue · "+RunController.StateLabel(r.State);
        RefreshCountdown();
    }
    private void RefreshCountdown()
    {
        if (!_ready || _smoke) return;
        var r=_controller.Data.Run;
        ResetText.Text=r.State == RunState.WaitingLimit ? r.WaitUntil is { } until ? "Восстановление всех исчерпанных окон: "+until.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") : "Время восстановления неизвестно" : "Время восстановления: —";
        if (r.State == RunState.WaitingLimit && r.WaitUntil is { } reset)
        {
            var left=reset-DateTimeOffset.UtcNow;
            CountdownText.Text=left<=TimeSpan.Zero ? "Время наступило. Требуется проверка сервера." : $"Осталось: {(int)left.TotalDays} д {left.Hours:00}:{left.Minutes:00}:{left.Seconds:00}";
        }
        else CountdownText.Text="Обратный отсчёт: —";
        NextCheckText.Text=r.State == RunState.WaitingLimit && !r.Paused && !r.UserStopped ? "Следующая проверка: "+(r.NextCheck?.ToLocalTime().ToString("HH:mm:ss") ?? "сейчас") : "";
        if (r.State == RunState.WaitingLimit && r.Detail.StartsWith("Нет интернета",StringComparison.Ordinal)) StateText.Text="Нет интернета";
    }
    private static void Append(TextBox box,string value)
    {
        if (box.Text.Length>500000) box.Text=box.Text[^250000..];
        box.AppendText(Redactor.Clean(value)); box.ScrollToEnd();
    }
    private void WriteJournal(string message)
    {
        var safe=Redactor.Clean(message);
        Append(JournalBox,$"{DateTimeOffset.Now:HH:mm:ss}  {safe}\n");
        try { _log.Write(safe); } catch (IOException) { }
    }
    private void Mode_Changed(object sender,RoutedEventArgs e)
    { if (NewPanel is null || ExistingPanel is null) return; NewPanel.Visibility=NewMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; ExistingPanel.Visibility=ExistingMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
    private void BrowseProject_Click(object sender,RoutedEventArgs e)
    { var dlg=new OpenFolderDialog { Title="Папка проекта" }; if (dlg.ShowDialog(this) == true) ProjectBox.Text=dlg.FolderName; }
    private void BrowseCli_Click(object sender,RoutedEventArgs e)
    { var dlg=new OpenFileDialog { Filter="Codex CLI (codex.exe)|codex.exe|EXE (*.exe)|*.exe",Title="Нативный Codex CLI" }; if (dlg.ShowDialog(this) == true) CliPathBox.Text=dlg.FileName; }
    private async void Start_Click(object sender,RoutedEventArgs e) => await RunAction(async ()=>
    { await SaveSettings(); if (NewMode.IsChecked == true) await _controller.StartNewAsync(ProjectBox.Text.Trim(),PromptBox.Text,AutoBox.IsChecked == true); else await _controller.TakeOverAsync(SessionIdBox.Text.Trim(),HandoffBox.IsChecked == true,AutoBox.IsChecked == true); });
    private async void EnableAuto_Click(object sender,RoutedEventArgs e) => await RunAction(async ()=> { await SaveSettings(); await _controller.EnableAutoAsync(); AutoBox.IsChecked=true; });
    private async void Pause_Click(object sender,RoutedEventArgs e) => await RunAction(_controller.PauseAsync);
    private async void Stop_Click(object sender,RoutedEventArgs e) => await RunAction(_controller.StopAsync);
    private async void Continue_Click(object sender,RoutedEventArgs e) => await RunAction(async ()=> { await SaveSettings(); await _controller.ContinueNowAsync(); });
    private async void Reconcile_Click(object sender,RoutedEventArgs e) => await RunAction(_controller.ReconcileAsync);
    private async void CheckLimits_Click(object sender,RoutedEventArgs e) => await RunAction(_controller.CheckLimitsAsync);
    private async void Connect_Click(object sender,RoutedEventArgs e) => await RunAction(Connect);
    private async void Login_Click(object sender,RoutedEventArgs e) => await RunAction(()=>_controller.LoginAsync(OpenExternal));
    private async void SaveSettings_Click(object sender,RoutedEventArgs e) => await RunAction(async ()=> { await SaveSettings(); WriteJournal("Настройки сохранены."); });
    private async void Reply_Click(object sender,RoutedEventArgs e) => await RunAction(async ()=> { await _controller.ReplyAsync(ReplyBox.Text); ReplyBox.Clear(); });
    private async void Sessions_Click(object sender,RoutedEventArgs e) => await RunAction(async ()=>
    {
        var list=await _controller.ListSessionsAsync(); var dialog=new SessionPicker(list) { Owner=this };
        if (dialog.ShowDialog() == true && dialog.Selected is { } t)
        { SessionIdBox.Text=t.Id; ProjectBox.Text=t.Cwd; HandoffBox.IsChecked=false; SessionDetail.Text=t.Name+"\n"+t.Cwd+"\n"+t.LocalUpdated+" · "+t.StatusLabel+" · "+t.Source; }
    });
    private async void Approval_Click(object sender,RoutedEventArgs e) => await RunAction(async ()=>
    {
        var req=_controller.PendingRequests.FirstOrDefault(); if (req is null) return;
        var dialog=new ApprovalDialog(req) { Owner=this };
        if (dialog.ShowDialog() == true && dialog.Response is { } response) await _controller.RespondAsync(req,response);
    });
    private static void OpenExternal(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute=true });
    private void OpenProject_Click(object sender,RoutedEventArgs e)
    { var path=string.IsNullOrWhiteSpace(_controller.Data.Run.ProjectPath) ? ProjectBox.Text : _controller.Data.Run.ProjectPath; if (Directory.Exists(path)) OpenExternal(path); }
    private void OpenLog_Click(object sender,RoutedEventArgs e) { Directory.CreateDirectory(_store.DirectoryPath); OpenExternal(_store.DirectoryPath); }
    private void ClearLog_Click(object sender,RoutedEventArgs e) { _log.Clear(); JournalBox.Clear(); OutputBox.Clear(); }
    private void Docs_Click(object sender,RoutedEventArgs e) => OpenExternal("https://learn.chatgpt.com/docs/app-server");
    private void Hide_Click(object sender,RoutedEventArgs e) => Hide();
    private void ShowWindow() { Show(); WindowState=WindowState.Normal; Activate(); }
    private async void Exit_Click(object sender,RoutedEventArgs e) => await ExitAsync();
    private async void Window_Closing(object? sender,CancelEventArgs e)
    {
        if (_exit) return;
        e.Cancel=true;
        if (_controller.Data.Settings.CloseToTray) Hide(); else await ExitAsync();
    }
    private async Task ExitAsync()
    {
        if (_exit) return; _exit=true; _timer.Stop();
        SystemEvents.PowerModeChanged-=PowerModeChanged;
        try { await _controller.DisposeAsync(); }
        finally { _tray.Visible=false; _tray.Dispose(); Close(); Application.Current.Shutdown(_smokeFailed ? 1 : 0); }
    }
    private void PowerModeChanged(object sender,PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(async ()=> { if (!_tickBusy) { _tickBusy=true; try { await _controller.TickAsync(true); } finally { _tickBusy=false; } } }); }

    private async Task SmokeTestAsync()
    {
        try
        {
            ProjectBox.Text=@"C:\Проекты с пробелами\пример";
            PromptBox.Text="Проверь проект и выполни оставшиеся пункты задачи.";
            StateText.Text="Ожидание лимита"; DetailText.Text="Проверка интерфейса. Следующее продолжение будет только после подтверждения сервера.";
            CliStatusText.Text="Codex CLI · тест интерфейса"; AuthText.Text="ChatGPT · проверка интеграции выполнена отдельно";
            var rates=RateLimits.Parse(JsonDocument.Parse("{\"ordinaryUsageAllowed\":false,\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":100,\"windowDurationMins\":300,\"resetsAt\":1791600000},\"secondary\":{\"usedPercent\":30,\"windowDurationMins\":10080,\"resetsAt\":1791983809}}}").RootElement);
            LimitsItems.ItemsSource=rates.Windows;
            ResetText.Text="Восстановление: локальное время Windows"; CountdownText.Text="Осталось: 00:42:18";
            Append(OutputBox,"Задача ожидает восстановления лимита. Уже выполненные изменения сохраняются.\n");
            WriteJournal("Проверка русскоязычного интерфейса, рендеринга и трея.");
            await Task.Delay(500); UpdateLayout();
            var report=Environment.GetEnvironmentVariable("CODEX_AUTOCONTINUE_SMOKE_DIR") ?? _store.DirectoryPath;
            Directory.CreateDirectory(report);
            Capture(Path.Combine(report,"interface.png"));
            var check=new StringBuilder(); check.AppendLine($"Window: {ActualWidth}x{ActualHeight}; tray: {_tray.Visible}; Russian controls: OK");
            Hide(); check.AppendLine("Hide to tray: "+(!IsVisible)); ((Forms.ToolStripMenuItem)_tray.ContextMenuStrip!.Items[0]).PerformClick(); check.AppendLine("Restore through tray menu: "+IsVisible);
            if (!IsVisible || !_tray.Visible) throw new InvalidOperationException("Tray hide/restore failed.");
            var dpi=VisualTreeHelper.GetDpi(this); check.AppendLine($"Current Windows DPI: {dpi.PixelsPerInchX}x{dpi.PixelsPerInchY}; manifest: PerMonitorV2");
            var dense=new RenderTargetBitmap((int)Math.Ceiling(ActualWidth*1.5),(int)Math.Ceiling(ActualHeight*1.5),144,144,PixelFormats.Pbgra32); dense.Render(this);
            var denseEncoder=new PngBitmapEncoder(); denseEncoder.Frames.Add(BitmapFrame.Create(dense)); using (var stream=File.Create(Path.Combine(report,"interface-150pct.png"))) denseEncoder.Save(stream);
            check.AppendLine("150% vector rendering: captured");
            Width=1050; Height=710; UpdateLayout(); Capture(Path.Combine(report,"interface-compact.png"));
            check.AppendLine("Compact window 1050x710: rendered");
            var request=new ServerRequest(JsonExt.ToJson(1),"item/commandExecution/requestApproval",JsonExt.ToJson(new { threadId="test", turnId="turn", itemId="item",command="dotnet test \"C:\\Проект с пробелами\"", reason="Требуется разрешение",cwd=@"C:\Проект с пробелами" }));
            var approval=new ApprovalDialog(request) { Owner=this }; approval.Show(); approval.UpdateLayout();
            var image=new RenderTargetBitmap((int)approval.ActualWidth,(int)approval.ActualHeight,96,96,PixelFormats.Pbgra32); image.Render(approval);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var stream=File.Create(Path.Combine(report,"approval.png"))) encoder.Save(stream);
            approval.Close(); check.AppendLine("Approval dialog: rendered");
            var sessionPicker=new SessionPicker(new[] { new SessionInfo("test-id","Задача с кириллицей",@"C:\Проект с пробелами",DateTimeOffset.Now,"notLoaded","vscode","gpt-6.1-sol") }) { Owner=this };
            sessionPicker.Show(); sessionPicker.UpdateLayout();
            var sessionBitmap=new RenderTargetBitmap((int)sessionPicker.ActualWidth,(int)sessionPicker.ActualHeight,96,96,PixelFormats.Pbgra32); sessionBitmap.Render(sessionPicker);
            var sessionEncoder=new PngBitmapEncoder(); sessionEncoder.Frames.Add(BitmapFrame.Create(sessionBitmap)); using (var stream=File.Create(Path.Combine(report,"sessions.png"))) sessionEncoder.Save(stream);
            sessionPicker.Close(); check.AppendLine("Session picker: rendered");
            using (var runKey=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                var oldValue=runKey.GetValue("CodexAutoContinue"); var oldKind=oldValue is null ? RegistryValueKind.String : runKey.GetValueKind("CodexAutoContinue");
                try
                {
                    SetStartup(true); var enabled=(string?)runKey.GetValue("CodexAutoContinue");
                    if (enabled != "\""+Environment.ProcessPath+"\" --tray") throw new InvalidOperationException("Startup registration mismatch.");
                    SetStartup(false); if (runKey.GetValue("CodexAutoContinue") is not null) throw new InvalidOperationException("Startup removal failed.");
                    check.AppendLine("Startup registry: enabled/verified/removed; original value restored if present");
                }
                finally { if (oldValue is not null) runKey.SetValue("CodexAutoContinue",oldValue,oldKind); else runKey.DeleteValue("CodexAutoContinue",false); }
            }
            File.WriteAllText(Path.Combine(report,"ui-smoke.txt"),check.ToString(),Encoding.UTF8);
        }
        catch (Exception e) { _smokeFailed=true; var report=Environment.GetEnvironmentVariable("CODEX_AUTOCONTINUE_SMOKE_DIR") ?? _store.DirectoryPath; Directory.CreateDirectory(report); File.WriteAllText(Path.Combine(report,"smoke-error.txt"),e.ToString()); }
        finally { await ExitAsync(); }
    }
    private void Capture(string path)
    {
        var bitmap=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(this);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var fs=File.Create(path); encoder.Save(fs);
    }
}
