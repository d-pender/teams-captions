// Teams Captions: a window and a notification-area icon around CaptionWatcher.
// Built with the C# compiler that comes with Windows (see Build-App.ps1), so it is kept to C# 5.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

// The compiler that comes with Windows doesn't add this. Without it WPF runs in an old
// compatibility mode that draws at the main screen's scaling on every monitor, so the
// window came out double size on a 100% screen next to a 200% one.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Teams Captions")]
[assembly: AssemblyProduct("Teams Captions")]
[assembly: AssemblyDescription("Saves Microsoft Teams live captions for later review")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace TeamsCaptions.App
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Options options = Options.Parse(args);
            // The same lock as Save-TeamsCaptions.ps1, so the two never save the same meeting twice.
            string key = CaptionWatcher.LockName(options.ProcessName);
            using (var mutex = new Mutex(false, key))
            {
                bool owned;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    // Already running: bring up that window, unless the console saver holds the lock.
                    EventWaitHandle running;
                    if (EventWaitHandle.TryOpenExisting(key + "-show", out running))
                    {
                        running.Set();
                        running.Dispose();
                        return 0;
                    }
                    MessageBox.Show("Teams captions are already being saved in a console window (Save-TeamsCaptions.ps1). Close that window, then start Teams Captions again.",
                        "Teams Captions", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 1;
                }
                try
                {
                    using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, key + "-show"))
                    using (var exit = new EventWaitHandle(false, EventResetMode.AutoReset, key + "-exit"))
                    {
                        Forms.Application.EnableVisualStyles();
                        var app = new Application();
                        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        var controller = new Controller(app, options, show, exit);
                        app.Startup += delegate { controller.Start(); };
                        return app.Run();
                    }
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }

    // Command line. /tray starts hidden in the notification area (the sign-in shortcut uses
    // it); the rest are mainly for tests. Only what is given overrides the watcher's defaults.
    internal sealed class Options
    {
        public bool Tray;
        public string OutDir;
        public string ProcessName = CaptionWatcher.DefaultProcessName;
        public string WindowTitle;
        public int? GraceSeconds;
        public int? SaveEverySeconds;
        public int PollMs = 1000;
        public string LogPath;
        public string RawLogPath;
        public string SettingsPath;

        public void ApplyTo(CaptionWatcher watcher)
        {
            watcher.ProcessName = ProcessName;
            if (WindowTitle != null) watcher.WindowTitle = WindowTitle;
            if (GraceSeconds.HasValue) watcher.GraceSeconds = GraceSeconds.Value;
            if (SaveEverySeconds.HasValue) watcher.SaveEverySeconds = SaveEverySeconds.Value;
            watcher.RawLogPath = RawLogPath;
        }

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i].TrimStart('/', '-').ToLowerInvariant();
                string value = i + 1 < args.Length ? args[i + 1] : null;
                switch (name)
                {
                    case "tray": o.Tray = true; continue;
                    case "out": o.OutDir = value; break;
                    case "process": o.ProcessName = value; break;
                    case "window-title": o.WindowTitle = value; break;
                    case "grace": o.GraceSeconds = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "save": o.SaveEverySeconds = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "poll": o.PollMs = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "log": o.LogPath = value; break;
                    case "raw": o.RawLogPath = value; break;
                    case "settings": o.SettingsPath = value; break;
                    default: continue;
                }
                i++;
            }
            return o;
        }
    }

    // %APPDATA%\Teams Captions\settings.ini, as Name=value lines.
    internal sealed class Settings
    {
        public string OutDir;
        public bool TrayTipShown;
        public double WindowWidth;    // in device-independent units; 0 = the default
        public double WindowHeight;
        private readonly string path;

        public Settings(string path)
        {
            this.path = path;
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string name = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (name == "OutDir" && value.Length > 0) OutDir = value;
                else if (name == "TrayTipShown") TrayTipShown = value == "1";
                else if (name == "WindowWidth") double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out WindowWidth);
                else if (name == "WindowHeight") double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out WindowHeight);
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new[]
            {
                "OutDir=" + (OutDir ?? string.Empty),
                "TrayTipShown=" + (TrayTipShown ? "1" : "0"),
                "WindowWidth=" + WindowWidth.ToString("0", CultureInfo.InvariantCulture),
                "WindowHeight=" + WindowHeight.ToString("0", CultureInfo.InvariantCulture)
            });
        }
    }

    // Status lines with times, in the same form as the console saver prints them.
    internal sealed class Log
    {
        private readonly string path;
        private readonly object gate = new object();

        public Log(string path)
        {
            this.path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 1024 * 1024)
            {
                string old = path + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
        }

        public void Write(string message)
        {
            lock (gate)
            {
                try
                {
                    File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
                }
                catch (IOException) { }               // logging must never stop the saving
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static class Shortcut
    {
        public static void Create(string path, string target, string arguments, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object link = null;
            try
            {
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type t = link.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                t.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { arguments });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { description });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    // What the window asks the watcher thread to do.
    internal enum CommandKind { Stop, Start, SetFolder, Exit }

    internal sealed class Command
    {
        public readonly CommandKind Kind;
        public readonly string Folder;   // for SetFolder

        public Command(CommandKind kind, string folder)
        {
            Kind = kind;
            Folder = folder;
        }
    }

    internal sealed class Controller
    {
        private static readonly SolidColorBrush SpeakerBrush = Frozen(0x3E, 0x44, 0xA6);
        private static readonly SolidColorBrush TimeBrush = Frozen(0x80, 0x86, 0x8B);

        private readonly Application app;
        private readonly Options options;
        private readonly EventWaitHandle showEvent;
        private readonly EventWaitHandle exitEvent;
        private readonly Settings settings;
        private readonly Log log;
        private readonly string exePath = Assembly.GetEntryAssembly().Location;
        private readonly object gate = new object();
        private readonly Queue<Command> commands = new Queue<Command>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private string outDir;
        private Thread worker;
        private Window window;
        private Forms.NotifyIcon tray;
        private Forms.ToolStripMenuItem startStopItem;
        private WatchStatus shown = new WatchStatus { State = WatchState.Waiting, Recent = new Entry[0] };
        private string balloonPath;
        private bool exiting;

        // Named elements in MainWindow.xaml.
        private System.Windows.Shapes.Ellipse statusDot;
        private TextBlock statusText, detailText, folderText, emptyHint;
        private StackPanel linesPanel;
        private ScrollViewer linesScroll;
        private Button startStopButton, openFileButton, openFolderButton, changeFolderButton;
        private CheckBox signInCheck;

        public Controller(Application app, Options options, EventWaitHandle showEvent, EventWaitHandle exitEvent)
        {
            this.app = app;
            this.options = options;
            this.showEvent = showEvent;
            this.exitEvent = exitEvent;
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            settings = new Settings(options.SettingsPath ?? Path.Combine(roaming, "Teams Captions", "settings.ini"));
            log = new Log(options.LogPath ?? Path.Combine(local, "Teams Captions", "TeamsCaptions.log"));
        }

        public void Start()
        {
            app.DispatcherUnhandledException += OnUiError;
            app.SessionEnding += delegate { Exit(); };
            outDir = options.OutDir ?? settings.OutDir ?? TranscriptFile.DefaultFolder;
            log.Write("Teams Captions started; transcripts go to " + outDir);

            BuildWindow();
            BuildTray();
            StartSignalThread();

            worker = new Thread(Work);
            worker.Name = "Caption watcher";
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();

            if (!options.Tray) ShowWindow();
            else if (!settings.TrayTipShown) ShowTrayTip();
        }

        // ---- watcher thread -------------------------------------------------------------

        private void Work()
        {
            var watcher = new CaptionWatcher();
            options.ApplyTo(watcher);
            watcher.OutDir = outDir;
            watcher.Watch = true;
            watcher.Message += log.Write;
            watcher.Trace += delegate (string m) { log.Write("(" + m + ")"); };
            watcher.Changed += delegate (WatchStatus s) { Ui(delegate { Show(s); }); };
            watcher.SessionStarted += delegate (string title, string path) { Ui(delegate { OnStarted(title); }); };
            watcher.SessionSaved += delegate (string title, string path, int lines) { Ui(delegate { OnSaved(title, path, lines); }); };
            while (true)
            {
                Command[] pending;
                lock (gate)
                {
                    pending = commands.ToArray();
                    commands.Clear();
                }
                foreach (Command c in pending)
                {
                    switch (c.Kind)
                    {
                        case CommandKind.Exit:
                            try { watcher.Shutdown(); }
                            catch (Exception ex) { log.Write("Error while saving on exit: " + ex.Message); }
                            log.Write("Teams Captions closed.");
                            return;
                        case CommandKind.Stop:
                            watcher.Stop();
                            log.Write("Stopped saving.");
                            break;
                        case CommandKind.Start:
                            watcher.Start();
                            log.Write("Started saving again.");
                            break;
                        case CommandKind.SetFolder:
                            watcher.OutDir = c.Folder;
                            log.Write("Transcripts from now on go to " + c.Folder);
                            break;
                    }
                }
                int wait = options.PollMs;
                try { watcher.Poll(DateTime.Now); }
                catch (Exception ex)
                {
                    log.Write("Error: " + ex);
                    wait = 5000;
                }
                wake.WaitOne(wait);
            }
        }

        private void Send(CommandKind kind, string folder)
        {
            lock (gate) commands.Enqueue(new Command(kind, folder));
            wake.Set();
        }

        private void Ui(Action action)
        {
            app.Dispatcher.BeginInvoke(action);
        }

        // Another launch (show) or the installer (exit) can signal this copy.
        private void StartSignalThread()
        {
            var thread = new Thread(delegate ()
            {
                var handles = new WaitHandle[] { showEvent, exitEvent };
                while (true)
                {
                    if (WaitHandle.WaitAny(handles) == 0) Ui(ShowWindow);
                    else
                    {
                        Ui(Exit);
                        return;
                    }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        // ---- window ---------------------------------------------------------------------

        private void BuildWindow()
        {
            using (Stream xaml = Resource("MainWindow.xaml")) window = (Window)XamlReader.Load(xaml);
            using (Stream icon = Resource("TeamsCaptions.ico"))
                window.Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (settings.WindowWidth >= window.MinWidth) window.Width = settings.WindowWidth;
            if (settings.WindowHeight >= window.MinHeight) window.Height = settings.WindowHeight;
            statusDot = (System.Windows.Shapes.Ellipse)window.FindName("StatusDot");
            statusText = (TextBlock)window.FindName("StatusText");
            detailText = (TextBlock)window.FindName("DetailText");
            folderText = (TextBlock)window.FindName("FolderText");
            emptyHint = (TextBlock)window.FindName("EmptyHint");
            linesPanel = (StackPanel)window.FindName("LinesPanel");
            linesScroll = (ScrollViewer)window.FindName("LinesScroll");
            startStopButton = (Button)window.FindName("StartStopButton");
            openFileButton = (Button)window.FindName("OpenFileButton");
            openFolderButton = (Button)window.FindName("OpenFolderButton");
            changeFolderButton = (Button)window.FindName("ChangeFolderButton");
            signInCheck = (CheckBox)window.FindName("SignInCheck");

            window.Closing += OnClosing;
            startStopButton.Click += delegate { ToggleSaving(); };
            openFileButton.Click += delegate { OpenTranscript(); };
            openFolderButton.Click += delegate { OpenFolder(); };
            changeFolderButton.Click += delegate { ChangeFolder(); };
            signInCheck.IsChecked = File.Exists(SignInLink());
            signInCheck.Checked += delegate { SetSignIn(true); };
            signInCheck.Unchecked += delegate { SetSignIn(false); };
            ShowFolder();
            Show(shown);
        }

        private void Show(WatchStatus s)
        {
            shown = s;
            string headline, detail, tip;
            Color dot;
            switch (s.State)
            {
                case WatchState.Saving:
                    dot = Color.FromRgb(0x1E, 0x8E, 0x3E);
                    headline = "Saving captions";
                    detail = s.Title + "  \u00B7  " + LinesText(s.Lines);
                    tip = "saving " + s.Title;
                    break;
                case WatchState.Away:
                    dot = Color.FromRgb(0xE3, 0x8B, 0x00);
                    headline = "Captions paused";
                    detail = s.Title + "  \u00B7  " + LinesText(s.Lines) + ". The captions went away; this transcript stays open for " +
                        Duration(options.GraceSeconds ?? CaptionWatcher.DefaultGraceSeconds) + " in case they come back.";
                    tip = "captions paused";
                    break;
                case WatchState.Stopped:
                    dot = Color.FromRgb(0xC5, 0x22, 0x1F);
                    headline = "Not saving";
                    detail = "Press Start to save captions again.";
                    tip = "not saving";
                    break;
                default:
                    dot = Color.FromRgb(0x9A, 0xA0, 0xA6);
                    headline = "Waiting for live captions";
                    detail = "Turn on live captions in a Teams meeting (More > Language and speech). Saving starts by itself.";
                    tip = "waiting for live captions";
                    break;
            }
            statusDot.Fill = new SolidColorBrush(dot);
            statusText.Text = headline;
            detailText.Text = detail;
            bool stopped = s.State == WatchState.Stopped;
            startStopButton.Content = stopped ? "Start" : "Stop";
            if (startStopItem != null) startStopItem.Text = stopped ? "Start saving" : "Stop saving";
            openFileButton.IsEnabled = (s.Path ?? s.LastPath) != null;
            if (tray != null) SetTrayText("Teams Captions: " + tip);
            if (window.IsVisible) ShowLines(s.Recent);
        }

        // One paragraph per speaker turn, as in the transcript.
        private void ShowLines(Entry[] lines)
        {
            bool atEnd = linesScroll.VerticalOffset >= linesScroll.ScrollableHeight - 4;
            linesPanel.Children.Clear();
            Entry head = null;
            TextBlock block = null;
            foreach (Entry line in lines)
            {
                if (CaptionLog.StartsTurn(head, line))
                {
                    block = new TextBlock();
                    block.TextWrapping = TextWrapping.Wrap;
                    block.Margin = new Thickness(0, 0, 0, 10);
                    var who = new Run(line.Speaker);
                    who.FontWeight = FontWeights.SemiBold;
                    who.Foreground = SpeakerBrush;
                    var when = new Run("   " + line.FirstSeen.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                    when.Foreground = TimeBrush;
                    when.FontSize = 11;
                    block.Inlines.Add(who);
                    block.Inlines.Add(when);
                    block.Inlines.Add(new LineBreak());
                    block.Inlines.Add(new Run(line.Text));
                    linesPanel.Children.Add(block);
                    head = line;
                }
                else block.Inlines.Add(new Run(" " + line.Text));
            }
            emptyHint.Visibility = lines.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (atEnd) linesScroll.ScrollToEnd();
        }

        private void ShowFolder()
        {
            folderText.Text = DisplayPath(outDir);
            folderText.ToolTip = outDir;
        }

        // Relative to Documents or the user folder when it's under them: shorter, and it keeps
        // the user name out of screenshots.
        private static string DisplayPath(string path)
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments).TrimEnd('\\');
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
            if (path.StartsWith(docs + "\\", StringComparison.OrdinalIgnoreCase)) return "Documents" + path.Substring(docs.Length);
            if (path.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase)) return path.Substring(home.Length + 1);
            return path;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        // Never wider or taller than 90% of the screen it opens on. WPF centres a new window on
        // the screen with the mouse, and a laptop screen at 200% has only about 800 units of height.
        private void FitToScreen()
        {
            Drawing.Point mouse = Forms.Control.MousePosition;
            Forms.Screen screen = Forms.Screen.FromPoint(mouse);
            var pt = new POINT { X = mouse.X, Y = mouse.Y };
            uint dpiX, dpiY;
            double scale = GetDpiForMonitor(MonitorFromPoint(pt, 2), 0, out dpiX, out dpiY) == 0 ? dpiY / 96.0 : 1.0;
            double maxWidth = screen.WorkingArea.Width / scale * 0.9, maxHeight = screen.WorkingArea.Height / scale * 0.9;
            if (window.Width > maxWidth) window.Width = Math.Max(window.MinWidth, maxWidth);
            if (window.Height > maxHeight) window.Height = Math.Max(window.MinHeight, maxHeight);
        }

        private void RememberSize()
        {
            if (window.WindowState != WindowState.Normal || window.ActualWidth <= 0) return;
            settings.WindowWidth = Math.Round(window.ActualWidth);
            settings.WindowHeight = Math.Round(window.ActualHeight);
            SaveSettings();
        }

        private void ShowWindow()
        {
            if (exiting) return;
            if (!window.IsVisible)
            {
                FitToScreen();
                window.Show();
            }
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            ShowLines(shown.Recent);
            linesScroll.ScrollToEnd();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (exiting) return;
            // Closing the window keeps saving; Exit is in the notification-area menu.
            e.Cancel = true;
            RememberSize();
            window.Hide();
            if (!settings.TrayTipShown) ShowTrayTip();
        }

        private void ToggleSaving()
        {
            Send(shown.State == WatchState.Stopped ? CommandKind.Start : CommandKind.Stop, null);
        }

        private void OpenTranscript()
        {
            string path = shown.Path ?? shown.LastPath;
            if (path != null && File.Exists(path)) Open(path);
            else OpenFolder();
        }

        private void OpenFolder()
        {
            Directory.CreateDirectory(outDir);
            Process.Start("explorer.exe", "\"" + outDir + "\"");
        }

        private void Open(string path)
        {
            try { Process.Start(path); }
            catch (Win32Exception) { Process.Start("explorer.exe", "/select,\"" + path + "\""); }   // no app for .md files
        }

        private void ChangeFolder()
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Choose the folder for Teams caption transcripts.";
                dialog.SelectedPath = outDir;
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
                outDir = dialog.SelectedPath;
            }
            settings.OutDir = outDir;
            SaveSettings();
            ShowFolder();
            Send(CommandKind.SetFolder, outDir);
        }

        private static string SignInLink()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Teams Captions.lnk");
        }

        private void SetSignIn(bool on)
        {
            try
            {
                string link = SignInLink();
                if (on) Shortcut.Create(link, exePath, "/tray", "Saves Teams live captions from every meeting");
                else if (File.Exists(link)) File.Delete(link);
                log.Write(on ? "Will start at sign-in." : "Won't start at sign-in.");
            }
            catch (Exception ex)
            {
                log.Write("Could not change the sign-in shortcut: " + ex.Message);
                MessageBox.Show(window, "Couldn't change the sign-in shortcut: " + ex.Message, "Teams Captions",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveSettings()
        {
            try { settings.Save(); }
            catch (Exception ex) { log.Write("Could not save settings: " + ex.Message); }
        }

        // ---- notification area ----------------------------------------------------------

        private void BuildTray()
        {
            tray = new Forms.NotifyIcon();
            using (Stream icon = Resource("TeamsCaptions.ico")) tray.Icon = new Drawing.Icon(icon, Forms.SystemInformation.SmallIconSize);
            var menu = new Forms.ContextMenuStrip();
            var open = new Forms.ToolStripMenuItem("Open Teams Captions", null, delegate { ShowWindow(); });
            open.Font = new Drawing.Font(open.Font, Drawing.FontStyle.Bold);
            startStopItem = new Forms.ToolStripMenuItem("Stop saving", null, delegate { ToggleSaving(); });
            menu.Items.Add(open);
            menu.Items.Add(startStopItem);
            menu.Items.Add(new Forms.ToolStripMenuItem("Open transcripts folder", null, delegate { OpenFolder(); }));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, delegate { Exit(); }));
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate (object sender, Forms.MouseEventArgs e)
            {
                if (e.Button == Forms.MouseButtons.Left) ShowWindow();
            };
            tray.BalloonTipClicked += delegate
            {
                if (balloonPath != null && File.Exists(balloonPath)) Open(balloonPath);
                else ShowWindow();
            };
            SetTrayText("Teams Captions");
            tray.Visible = true;
            Show(shown);
        }

        private void SetTrayText(string text)
        {
            tray.Text = text.Length <= 63 ? text : text.Substring(0, 60) + "...";   // Windows allows 63 characters
        }

        private void ShowBalloon(string title, string text, string openPath)
        {
            balloonPath = openPath;
            tray.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.None);
        }

        private void ShowTrayTip()
        {
            ShowBalloon("Teams Captions keeps running here",
                "It saves live captions from every Teams meeting. Click the icon to open it; right-click to exit.", null);
            settings.TrayTipShown = true;
            SaveSettings();
        }

        private void OnStarted(string title)
        {
            if (!window.IsVisible) ShowBalloon("Saving Teams captions", title, null);
        }

        private void OnSaved(string title, string path, int lines)
        {
            ShowBalloon("Saved " + LinesText(lines), title + ". Click to open the transcript.", path);
        }

        // ---- exit and errors ------------------------------------------------------------

        private void Exit()
        {
            if (exiting) return;
            exiting = true;
            Send(CommandKind.Exit, null);
            if (window != null && window.IsVisible)
            {
                RememberSize();
                window.Hide();
            }
            if (worker != null && !worker.Join(15000)) log.Write("The watcher didn't stop within 15 seconds.");
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            if (window != null) window.Close();
            app.Shutdown();
        }

        private void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            log.Write("Error: " + e.Exception);
            MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nThe log is in %LOCALAPPDATA%\\Teams Captions.",
                "Teams Captions", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        // ---- helpers --------------------------------------------------------------------

        private static Stream Resource(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream("TeamsCaptions." + name);
        }

        private static string LinesText(int lines)
        {
            return lines == 1 ? "1 line" : lines.ToString(CultureInfo.InvariantCulture) + " lines";
        }

        private static string Duration(int seconds)
        {
            if (seconds >= 120) return (seconds / 60) + " minutes";
            if (seconds >= 60) return "a minute";
            return seconds + " seconds";
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
