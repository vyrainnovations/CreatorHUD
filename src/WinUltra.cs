// WinUltra: a glass Dynamic Island for Windows 11, in one exe.
//
// WinUltra.exe runs from wherever it is kept and never copies itself anywhere. On first run a setup
// window (Setup.cs) explains each part and lets the person choose: start when they sign in (a normal
// Start-up folder shortcut), a desktop shortcut, clipboard history, notification pop-ups, frosted glass
// and the Caps Lock pop-up. The tray icon switches any of these later, reopens the setup window, quits,
// or removes WinUltra. No admin rights are needed. Settings, clipboard pins and the shelf live in
// %LOCALAPPDATA%\WinUltra.
//
// Build.cmd compiles with /define:FRIENDS, which selects this opt-in behaviour. The code outside
// FRIENDS is the older self-installing variant and is not part of the published build.
//
// Extra command lines:
//   WinUltra.exe --open N                opens island tab N (0 Media, 1 Controls, 2 Stats, 3 Clipboard, 4 Shelf, 5 Timer)
//   WinUltra.exe --selftest out.txt      checks media, volume, brightness, radios, sensors and notifications

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("CreatorHUD")]
[assembly: AssemblyProduct("CreatorHUD")]
[assembly: AssemblyDescription("Liquid glass Dynamic Island for Windows 11, made for creators. Based on WinUltra.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#if FRIENDS
[assembly: AssemblyCompany("Vyra Innovations")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Gamindu Sasmitha (WinUltra); Eshan, Vyra Innovations (CreatorHUD). MIT License.")]
#endif

namespace WinUltra
{
    // What the tray menu switches on and off, saved in %LOCALAPPDATA%\WinUltra\settings.txt.
    class Options
    {
        public bool ShortcutsMade;
#if FRIENDS
        public bool ClipboardHistory = true, Notifications = true, FrostedGlass = true, CapsLock = false, SetupDone;
#endif
        // CreatorHUD: off by default, so the live glass can see past the island.
        public bool Recordable;
        // CreatorHUD: the older see-through window type (copied back from the GPU every frame). Only as
        // a fallback if the direct window misbehaves on some PC; takes effect after a restart.
        public bool ClassicWindow;

        static string FilePath { get { return Path.Combine(App.DataDir, "settings.txt"); } }

        public static Options Load()
        {
            var o = new Options();
            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var parts = line.Split('=');
                    if (parts.Length != 2) continue;
                    bool on = parts[1].Trim() == "1";
                    if (parts[0] == "shortcuts") o.ShortcutsMade = on;
                    if (parts[0] == "recordable") o.Recordable = on;
                    if (parts[0] == "classicwindow") o.ClassicWindow = on;
#if FRIENDS
                    if (parts[0] == "clipboard") o.ClipboardHistory = on;
                    if (parts[0] == "notifications") o.Notifications = on;
                    if (parts[0] == "glass") o.FrostedGlass = on;
                    if (parts[0] == "capslock") o.CapsLock = on;
                    if (parts[0] == "setup") o.SetupDone = on;
#endif
                }
            }
            catch (Exception) { }
            return o;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(App.DataDir);
#if FRIENDS
                File.WriteAllLines(FilePath, new[] { "clipboard=" + (ClipboardHistory ? 1 : 0), "notifications=" + (Notifications ? 1 : 0),
                    "glass=" + (FrostedGlass ? 1 : 0), "capslock=" + (CapsLock ? 1 : 0), "setup=" + (SetupDone ? 1 : 0), "recordable=" + (Recordable ? 1 : 0), "classicwindow=" + (ClassicWindow ? 1 : 0) });
#else
                File.WriteAllLines(FilePath, new[] { "shortcuts=" + (ShortcutsMade ? 1 : 0) });
#endif
            }
            catch (Exception) { }
        }
    }

    static class App
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        public const string IslandTitle = "WinUltra Island";
#if FRIENDS
        const string MutexName = @"Local\WinUltraFriends", QuitEventName = @"Local\WinUltraFriendsQuit";
#else
        const string MutexName = @"Local\WinUltra", QuitEventName = @"Local\WinUltraQuit";
#endif
#if !FRIENDS
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
#endif
        static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
#if FRIENDS
        // WINULTRA_DATA can point WinUltra at another data folder (handy for testing).
        public static readonly string DataDir = Environment.GetEnvironmentVariable("WINULTRA_DATA") ?? Path.Combine(LocalAppData, "WinUltra");
        public static string ExePath { get { return ThisExe; } }
        static bool removeData;
#else
        public static readonly string DataDir = Path.Combine(LocalAppData, "WinUltra");
#endif
        public static readonly string InstallDir = Path.Combine(LocalAppData, "Programs", "WinUltra");
        public static string InstalledExe { get { return Path.Combine(InstallDir, "WinUltra.exe"); } }
        static string ThisExe { get { return Process.GetCurrentProcess().MainModule.FileName; } }

        public static Options Options = new Options();
        static Island island;
        static Tray tray;
        static SynchronizationContext trayThread;

        [STAThread]
        static void Main(string[] args)
        {
            SetProcessDPIAware();

            if (args.Length >= 2 && args[0] == "--selftest") { SelfTest.Run(args[1]); return; }
            if (args.Length == 2 && args[0] == "--open")
            {
                int n;
                if (int.TryParse(args[1], out n)) OpenRunning(n);
                return;
            }

#if FRIENDS
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) { OpenRunning(-1); return; }    // already running: just open the island
                Options = Options.Load();
                bool firstRun = !Options.SetupDone;
                if (firstRun)
                {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    if (!SetupForm.Run(Options, true)) return;    // cancelled: nothing was changed
                }
                Run(firstRun);
            }
            if (removeData) FinishUninstall();
#else
            bool portable = args.Contains("--portable");
            if (!portable && !string.Equals(Path.GetFullPath(ThisExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            {
                Install();
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) { OpenRunning(-1); return; }    // already running: just open the island
                Run(args.Contains("--welcome"));
            }
#endif
        }

        static void Run(bool welcome)
        {
            Options = Options.Load();

            // The tray icon runs on its own thread with a WinForms message loop;
            // the island runs on this one with WPF.
            var ready = new ManualResetEvent(false);
            var thread = new Thread(TrayThread) { IsBackground = true, Name = "WinUltra tray" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start(ready);
            ready.WaitOne(5000);

            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) => { Store.Log(e.Exception.ToString()); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Store.Log(e.ExceptionObject.ToString());
            island = new Island();

            // A newer copy being installed asks this one to close.
            var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            ThreadPool.RegisterWaitForSingleObject(quit, delegate { Quit(); }, null, -1, true);

            if (welcome)
            {
                island.Loaded += delegate
                {
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    t.Tick += delegate { t.Stop(); island.ShowWelcome(); };
                    t.Start();
                };
            }

            app.Run(island);

            // Take the tray icon away before the process ends, or a dead icon stays in the tray.
            if (trayThread != null) trayThread.Send(delegate { if (tray != null) tray.Dispose(); }, null);
        }

        static void TrayThread(object ready)
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            trayThread = new System.Windows.Forms.WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(trayThread);
            try { tray = new Tray(); }
            catch (Exception ex) { Store.Log("tray thread: " + ex); }
            ((ManualResetEvent)ready).Set();
            System.Windows.Forms.Application.Run();
        }

        public static void OnIsland(Action a) { if (island != null) island.Dispatcher.BeginInvoke(a); }

        // ---------------------------------------------------------------- tray menu actions

        public static void OpenIsland() { OnIsland(delegate { island.OpenFromTray(); }); }

#if FRIENDS
        public static bool StartsWithWindows { get { return File.Exists(Shortcut.StartupLink); } }

        public static void SetStartup(bool on)
        {
            if (on) Shortcut.Make(Shortcut.StartupLink, ThisExe); else Shortcut.Remove(Shortcut.StartupLink);
        }

        public static void SetClipboardHistory(bool on) { Options.ClipboardHistory = on; Options.Save(); OnIsland(delegate { island.SetClipboardHistory(on); }); }
        public static void SetNotifications(bool on) { Options.Notifications = on; Options.Save(); OnIsland(delegate { island.SetNotifications(on); }); }
        public static void SetFrostedGlass(bool on) { Options.FrostedGlass = on; Options.Save(); OnIsland(delegate { island.SetFrostedGlass(on); }); }
        public static void SetCapsLock(bool on) { Options.CapsLock = on; Options.Save(); OnIsland(delegate { island.SetCapsLockPopup(on); }); }
        public static void SetRecordable(bool on) { Options.Recordable = on; Options.Save(); OnIsland(delegate { island.SetRecordable(on); }); }

        // The settings window again, from the tray. Applies whatever changed.
        public static void ShowSettings()
        {
            var before = new[] { Options.ClipboardHistory, Options.Notifications, Options.FrostedGlass, Options.CapsLock, Options.Recordable };
            if (!SetupForm.Run(Options, false)) return;
            if (before[0] != Options.ClipboardHistory) SetClipboardHistory(Options.ClipboardHistory);
            if (before[1] != Options.Notifications) SetNotifications(Options.Notifications);
            if (before[2] != Options.FrostedGlass) SetFrostedGlass(Options.FrostedGlass);
            if (before[3] != Options.CapsLock) SetCapsLock(Options.CapsLock);
            if (before[4] != Options.Recordable) SetRecordable(Options.Recordable);
        }

        public static void OpenDataFolder()
        {
            Directory.CreateDirectory(DataDir);
            Process.Start("explorer.exe", "\"" + DataDir + "\"");
        }

        public static void Uninstall()
        {
            var answer = System.Windows.Forms.MessageBox.Show(
                "Remove CreatorHUD?\n\nIt will stop starting when you sign in, remove its desktop shortcut, and delete its settings, clipboard pins and shelf list. "
                + "Then you can delete CreatorHUD.exe yourself.",
                "CreatorHUD", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question);
            if (answer != System.Windows.Forms.DialogResult.Yes) return;
            SetStartup(false);
            Shortcut.Remove(Shortcut.DesktopLink);
            removeData = true;
            Quit();
        }

        // After the island has closed: delete the data folder and show the exe so it can be deleted.
        static void FinishUninstall()
        {
            try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true); } catch (Exception) { }
            System.Windows.Forms.MessageBox.Show("WinUltra has been removed. You can now delete WinUltra.exe.", "WinUltra",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
            try { Process.Start("explorer.exe", "/select,\"" + ThisExe + "\""); } catch (Exception) { }
        }
#else
        public static bool StartsWithWindows
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("WinUltra") != null; }
                catch (Exception) { return false; }
            }
        }

        public static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("WinUltra", "\"" + ThisExeOrInstalled() + "\"");
                    else if (k.GetValue("WinUltra") != null) k.DeleteValue("WinUltra");
                }
            }
            catch (Exception ex) { Store.Log("startup: " + ex.Message); }
        }

        static string ThisExeOrInstalled() { return File.Exists(InstalledExe) ? InstalledExe : ThisExe; }
#endif

        public static void Quit() { OnIsland(delegate { island.Close(); }); }

#if !FRIENDS
        public static void Uninstall()
        {
            var answer = System.Windows.Forms.MessageBox.Show(
                "Uninstall WinUltra?\n\nIt will stop starting with Windows, remove its shortcuts, and delete its settings, clipboard pins and shelf list.",
                "WinUltra", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question);
            if (answer != System.Windows.Forms.DialogResult.Yes) return;
            SetStartup(false);
            foreach (var link in new[] { DesktopShortcut, StartMenuShortcut })
                try { if (File.Exists(link)) File.Delete(link); } catch (Exception) { }
            try
            {
                // Remove the files a few seconds after this process has gone.
                var cmd = "/c timeout /t 3 /nobreak >nul & rmdir /s /q \"" + InstallDir + "\" & rmdir /s /q \"" + DataDir + "\"";
                Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Exception) { }
            Quit();
        }

        // ---------------------------------------------------------------- install and update

        static void Install()
        {
            try
            {
                bool firstInstall = !File.Exists(InstalledExe);

                // Close a copy that is already running, so its file can be replaced.
                try { using (var ev = EventWaitHandle.OpenExisting(QuitEventName)) ev.Set(); } catch (Exception) { }
                try
                {
                    using (var m = Mutex.OpenExisting(MutexName))
                    {
                        try { if (m.WaitOne(8000)) m.ReleaseMutex(); }
                        catch (AbandonedMutexException) { m.ReleaseMutex(); }
                    }
                }
                catch (Exception) { }

                Directory.CreateDirectory(InstallDir);
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Copy(ThisExe, InstalledExe, true); break; }
                    catch (IOException) { if (attempt >= 15) throw; Thread.Sleep(300); }
                }
                if (firstInstall || StartsWithWindows) SetStartup(true);

                // A desktop shortcut and a Start menu entry, made once: if you delete them they stay deleted.
                var options = Options.Load();
                if (!options.ShortcutsMade)
                {
                    MakeShortcut(DesktopShortcut);
                    MakeShortcut(StartMenuShortcut);
                    options.ShortcutsMade = true;
                    options.Save();
                }
                Process.Start(new ProcessStartInfo(InstalledExe, "--welcome") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("WinUltra couldn't install itself:\n\n" + ex.Message, "WinUltra",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
            }
        }

        static string DesktopShortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "WinUltra.lnk"); } }
        static string StartMenuShortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WinUltra.lnk"); } }

        // Uses Windows Script Host's shortcut maker, which every Windows 11 PC has.
        static void MakeShortcut(string path)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                var linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { InstalledExe });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { InstallDir });
                linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { InstalledExe + ",0" });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Open WinUltra" });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                Marshal.FinalReleaseComObject(link);
                Marshal.FinalReleaseComObject(shell);
            }
            catch (Exception ex) { Store.Log("shortcut: " + ex.Message); }
        }
#endif

        static void OpenRunning(int tab)
        {
            IntPtr h = Native.FindWindow(null, IslandTitle);
            if (h != IntPtr.Zero) Native.PostMessage(h, Native.RegisterWindowMessage("DynamicIslandOpenTab"), new IntPtr(tab), IntPtr.Zero);
        }
    }

    // The tray icon and its menu. Lives on the tray thread.
    class Tray : IDisposable
    {
        readonly System.Windows.Forms.NotifyIcon icon = new System.Windows.Forms.NotifyIcon();

        public Tray()
        {
            using (var s = typeof(Tray).Assembly.GetManifestResourceStream("WinUltra.ico"))
                icon.Icon = new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
            icon.Text = "CreatorHUD";

#if FRIENDS
            BuildMenu();
            icon.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) App.OpenIsland(); };
            icon.Visible = true;
        }

        void BuildMenu()
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var open = new System.Windows.Forms.ToolStripMenuItem("Open island", null, delegate { App.OpenIsland(); }) { ShortcutKeyDisplayString = "Ctrl+Alt+Space" };
            var clip =new System.Windows.Forms.ToolStripMenuItem("Clipboard history", null, delegate { App.SetClipboardHistory(!App.Options.ClipboardHistory); });
            var notes = new System.Windows.Forms.ToolStripMenuItem("Notification pop-ups", null, delegate { App.SetNotifications(!App.Options.Notifications); });
            var glass = new System.Windows.Forms.ToolStripMenuItem("Frosted glass", null, delegate { App.SetFrostedGlass(!App.Options.FrostedGlass); });
            var caps = new System.Windows.Forms.ToolStripMenuItem("Caps Lock pop-up", null, delegate { App.SetCapsLock(!App.Options.CapsLock); });
            var rec = new System.Windows.Forms.ToolStripMenuItem("Show in screenshots and recordings", null, delegate { App.SetRecordable(!App.Options.Recordable); });
            var classic = new System.Windows.Forms.ToolStripMenuItem("Compatibility mode (applies after restart)", null, delegate
            {
                App.Options.ClassicWindow = !App.Options.ClassicWindow;
                App.Options.Save();
            });
            var startup = new System.Windows.Forms.ToolStripMenuItem("Start when I sign in", null, delegate { App.SetStartup(!App.StartsWithWindows); });
            menu.Items.Add(open);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(clip);
            menu.Items.Add(notes);
            menu.Items.Add(glass);
            menu.Items.Add(caps);
            menu.Items.Add(rec);
            menu.Items.Add(classic);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(startup);
            menu.Items.Add("Settings...", null, delegate { App.ShowSettings(); });
            menu.Items.Add("Open data folder", null, delegate { App.OpenDataFolder(); });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Quit CreatorHUD", null, delegate { App.Quit(); });
            menu.Items.Add("Remove CreatorHUD...", null, delegate { App.Uninstall(); });
            menu.Opening += delegate
            {
                clip.Checked = App.Options.ClipboardHistory;
                notes.Checked = App.Options.Notifications;
                glass.Checked = App.Options.FrostedGlass;
                caps.Checked = App.Options.CapsLock;
                rec.Checked = App.Options.Recordable;
                classic.Checked = App.Options.ClassicWindow;
                startup.Checked = App.StartsWithWindows;
            };
            icon.ContextMenuStrip = menu;
#else
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var open = new System.Windows.Forms.ToolStripMenuItem("Open island", null, delegate { App.OpenIsland(); }) { ShortcutKeyDisplayString = "Ctrl+Alt+Space" };
            var startup = new System.Windows.Forms.ToolStripMenuItem("Start with Windows", null, delegate { App.SetStartup(!App.StartsWithWindows); });
            menu.Items.Add(open);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(startup);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Quit WinUltra", null, delegate { App.Quit(); });
            menu.Items.Add("Uninstall...", null, delegate { App.Uninstall(); });
            menu.Opening += delegate { startup.Checked = App.StartsWithWindows; };
            icon.ContextMenuStrip = menu;
            icon.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) App.OpenIsland(); };
            icon.Visible = true;
#endif
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }
    }
}
