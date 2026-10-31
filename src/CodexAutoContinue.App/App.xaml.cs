using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace CodexAutoContinue.App;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke=e.Args.Contains("--smoke-test");
        _instance=new Mutex(true,smoke ? @"Local\CodexAutoContinue.Smoke" : @"Local\CodexAutoContinue.Instance",out var created);
        if (!created) { MessageBox.Show("Codex AutoContinue уже запущен. Откройте окно через значок в трее.","Codex AutoContinue"); Shutdown(); return; }
        DispatcherUnhandledException += (_,args) => { MessageBox.Show("Ошибка: "+args.Exception.Message,"Codex AutoContinue"); args.Handled=true; };
        var root=smoke ? Path.Combine(Path.GetTempPath(),"CodexAutoContinue-smoke-"+Guid.NewGuid().ToString("N")) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexAutoContinue");
        var window=new MainWindow(root,smoke,e.Args.Contains("--tray"));
        MainWindow=window;
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
