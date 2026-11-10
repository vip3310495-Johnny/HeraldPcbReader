// Herald PCB Reader — Copyright (C) 2026 Johnny.ch.Tsai
// SPDX-License-Identifier: GPL-3.0-only
// Free software under the GNU General Public License version 3 (LICENSE.txt), WITHOUT ANY WARRANTY; with an
// additional permission to combine it with AvalonDock (Ms-PL), see NOTICE.txt.
using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace AdApp;

public partial class App : Application
{
    // per user, and both ends only for this user (PipeOptions.CurrentUserOnly): another account on the PC can neither
    // hand this program files to open nor stand in for it
    static readonly string PipeName = "HeraldPcbReader.open." + Environment.UserName;
    Mutex _mutex;

    /// <summary>Start-up timeline (ms since the process started), for tuning; automation shows it.</summary>
    public static readonly List<string> Marks = new();
    /// <summary>The same marks on the window's thread: its CPU time and the time the JIT took on it (ms), which do not
    /// swing with how long the graphics driver takes to answer.</summary>
    public static readonly List<string> CpuMarks = new();
    static readonly DateTime Started = System.Diagnostics.Process.GetCurrentProcess().StartTime;
    static readonly int UiThread = Environment.CurrentManagedThreadId;
    public static void Mark(string what)
    {
        lock (Marks) Marks.Add($"{what} {(DateTime.Now - Started).TotalMilliseconds:0}");
        if (Environment.CurrentManagedThreadId != UiThread || !GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u)) return;
        lock (CpuMarks) CpuMarks.Add($"{what} cpu {(k + u) / 10000} jit {System.Runtime.JitInfo.GetCompiledMethodCount(true)}/{System.Runtime.JitInfo.GetCompilationTime(true).TotalMilliseconds:0}");
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr thread, out long created, out long exited, out long kernel, out long user);
    /// <summary>Seconds since the process started.</summary>
    public static double SinceStart => (DateTime.Now - Started).TotalSeconds;

    protected override void OnStartup(StartupEventArgs e)
    {
        Mark("startup");
        base.OnStartup(e);
        // files and folders to open: everything but the switches and the values they take
        var paths = e.Args.Where((a, i) => !a.StartsWith("--") && !(i > 0 && e.Args[i - 1] is "--settings" or "--automation-port" or "--automation-token")
                                           && (File.Exists(a) || Directory.Exists(a))).ToList();
        int port = 0;
        for (int i = 0; i < e.Args.Length - 1; i++) if (e.Args[i] == "--automation-port") int.TryParse(e.Args[i + 1], out port);
        string token = null;
        for (int i = 0; i < e.Args.Length - 1; i++) if (e.Args[i] == "--automation-token") token = e.Args[i + 1];
        for (int i = 0; i < e.Args.Length - 1; i++) if (e.Args[i] == "--settings") Settings.DirOverride = e.Args[i + 1];
        // automation runs start empty unless asked to restore the last session (--restore)
        bool fresh = e.Args.Contains("--fresh") || port != 0 && !e.Args.Contains("--restore");

        _mutex = new Mutex(true, "HeraldPcbReader.singleton", out bool first);
        if (!first && port == 0)
        {
            // hand the paths to the running window and quit
            try
            {
                using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                c.Connect(1500);
                using var w = new StreamWriter(c);
                w.WriteLine(string.Join("\n", paths.Select(Path.GetFullPath)));
            }
            catch { }
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, ex) =>
        {
            CrashLog(ex.Exception);
            MessageBox.Show(ex.Exception.Message + "\n\n" + ex.Exception.StackTrace, AdApp.MainWindow.Product + " 發生錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        // errors that end the process (other threads) still leave a trace to report
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => CrashLog(ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { CrashLog(ex.Exception); ex.SetObserved(); };
        // the files (or the last session) start loading now, while the window is being built
        AdApp.MainWindow.BeginEarlyOpen(paths, fresh);
        var win = new MainWindow(paths, fresh);
        Mark("window built");
        MainWindow = win;
        win.Show();
        Mark("shown");
        if (port != 0) Automation.Start(win, port, token);
        if (first) StartPipe(win);
    }

    /// <summary>Appends to %APPDATA%\AltiumReader2\crash.log (kept under 1 MB).</summary>
    static void CrashLog(Exception e)
    {
        try
        {
            var path = Path.Combine(Settings.Dir, "crash.log");
            Directory.CreateDirectory(Settings.Dir);
            if (File.Exists(path) && new FileInfo(path).Length > 1 << 20) File.Delete(path);
            File.AppendAllText(path, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {AdApp.MainWindow.Product} {typeof(App).Assembly.GetName().Version}\n{e}\n\n");
        }
        catch { }
    }

    void StartPipe(MainWindow win)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    s.WaitForConnection();
                    using var r = new StreamReader(s);
                    var buf = new char[1 << 16]; int n = r.ReadBlock(buf, 0, buf.Length);   // paths only: 64 K characters is plenty
                    var list = new string(buf, 0, n).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                    Dispatcher.BeginInvoke(() => win.OpenFromOutside(list));
                }
                catch { Thread.Sleep(200); }
            }
        }) { IsBackground = true };
        t.Start();
    }
}
