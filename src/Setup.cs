// WinUltra's setup window and shortcuts (compiled with /define:FRIENDS, which Build.cmd sets).
// Everything WinUltra does is the person's choice: a setup window on first run explains each part
// and lets them switch it on or off, and the same window opens again from the tray icon.
// The exe runs from wherever it is kept; it never copies itself anywhere.

#if FRIENDS
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Forms;

namespace WinUltra
{
    // Windows' standard shortcut (.lnk) maker.
    static class Shortcut
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr data, int flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short key);
            void SetHotkey(short key);
            void GetShowCmd(out int show);
            void SetShowCmd(int show);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int icon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
            void Resolve(IntPtr hwnd, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        public static string StartupLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "WinUltra.lnk"); } }
        public static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "WinUltra.lnk"); } }

        public static void Make(string link, string target)
        {
            try
            {
                var shell = (IShellLinkW)new ShellLink();
                shell.SetPath(target);
                shell.SetWorkingDirectory(Path.GetDirectoryName(target));
                shell.SetIconLocation(target, 0);
                shell.SetDescription("WinUltra");
                ((IPersistFile)shell).Save(link, true);
                Marshal.FinalReleaseComObject(shell);
            }
            catch (Exception ex) { Store.Log("shortcut: " + ex.Message); }
        }

        public static void Remove(string link)
        {
            try { if (File.Exists(link)) File.Delete(link); } catch (Exception) { }
        }
    }

    // The setup window: what WinUltra does, and a switch for each part.
    class SetupForm : Form
    {
        readonly CheckBox startup, desktop, clipboard, notes, glass, caps, record, blender;

        public SetupForm(Options o, bool firstRun)
        {
            Text = firstRun ? "Welcome to CreatorHUD" : "CreatorHUD settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9.75f);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ShowInTaskbar = true;
            try
            {
                using (var s = typeof(SetupForm).Assembly.GetManifestResourceStream("WinUltra.ico")) Icon = new Icon(s);
            }
            catch (Exception) { }

            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(18, 14, 18, 10) };
            Controls.Add(flow);

            flow.Controls.Add(new Label { Text = firstRun ? "Welcome to CreatorHUD" : "CreatorHUD settings", Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
            flow.Controls.Add(Note("A Dynamic Island at the top of your screen with handy pop-ups. "
                + "Everything stays on this PC: CreatorHUD never connects to the internet."));

            flow.Controls.Add(Heading("Starting up"));
            startup = Box("Start CreatorHUD when I sign in", "Adds a shortcut to your Start-up folder.", App.StartsWithWindows || firstRun, flow);
            desktop = Box("Put a shortcut on my desktop", null, File.Exists(Shortcut.DesktopLink) || firstRun, flow);
            string folder = Path.GetDirectoryName(App.ExePath).ToLowerInvariant();
            if (folder.Contains(@"\downloads") || folder.Contains(@"\temp"))
                flow.Controls.Add(Note("Tip: the shortcuts point to where WinUltra.exe is now (" + Path.GetDirectoryName(App.ExePath)
                    + "). Move it to a folder you'll keep, such as Documents, and run it from there first."));

            flow.Controls.Add(Heading("Features (change them any time from the tray icon)"));
            clipboard = Box("Clipboard history", "Remembers the text and images you copy, on this PC only. Copies from password managers are skipped.", o.ClipboardHistory, flow);
            notes = Box("Notification pop-ups", "Shows your Windows notifications in the island.", o.Notifications, flow);
            glass = Box("Frosted glass", "Blurs the bit of screen behind the island so it looks like glass. Nothing is saved.", o.FrostedGlass, flow);
            caps = Box("Caps Lock pop-up", "Shows when Caps Lock turns on or off.", o.CapsLock, flow);

            blender = Box("Blender render progress", "While Blender renders, a small bubble beside the island shows how far it has got. "
                + "Needs the CreatorHUD Bridge add-on in Blender.", o.BlenderRing, flow);

            flow.Controls.Add(Heading("Screen recording"));
            record = Box("Show in screenshots and screen recordings",
                "Off: the island is hidden from recordings, so notifications never end up in your videos. "
                + "On: it shows up in your videos, live glass and all.", o.Recordable, flow);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0) };
            var ok = new Button { Text = firstRun ? "Start CreatorHUD" : "Save", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            flow.Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        static Label Heading(string text)
        {
            return new Label { Text = text, Font = new Font("Segoe UI Semibold", 10.5f), AutoSize = true, Margin = new Padding(0, 12, 0, 2) };
        }

        static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Color.FromArgb(90, 90, 100), Margin = new Padding(0, 0, 0, 4) };
        }

        static CheckBox Box(string text, string detail, bool on, FlowLayoutPanel flow)
        {
            var box = new CheckBox { Text = text, Checked = on, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            flow.Controls.Add(box);
            if (detail != null)
            {
                var note = Note(detail);
                note.Margin = new Padding(18, 0, 0, 2);
                flow.Controls.Add(note);
            }
            return box;
        }

        // Applies the choices. Returns false if the window was cancelled.
        public static bool Run(Options o, bool firstRun)
        {
            using (var f = new SetupForm(o, firstRun))
            {
                if (f.ShowDialog() != DialogResult.OK) return false;
                o.ClipboardHistory = f.clipboard.Checked;
                o.Notifications = f.notes.Checked;
                o.FrostedGlass = f.glass.Checked;
                o.CapsLock = f.caps.Checked;
                o.Recordable = f.record.Checked;
                o.BlenderRing = f.blender.Checked;
                o.SetupDone = true;
                o.Save();
                App.SetStartup(f.startup.Checked);
                if (f.desktop.Checked) Shortcut.Make(Shortcut.DesktopLink, App.ExePath); else Shortcut.Remove(Shortcut.DesktopLink);
                return true;
            }
        }
    }
}
#endif
