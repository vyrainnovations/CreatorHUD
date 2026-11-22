// Watches render.json, which the Blender add-on (blender\creator_hud_bridge.py) writes while a render
// runs, and tells the island what it says. Reading a tiny file twice a second costs next to nothing.

using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Threading;

class RenderStatus
{
    public string State = "idle";     // idle, rendering, complete, cancelled
    public string App = "Blender";
    public string File = "";
    public bool Animation;
    public int Frame, FramesDone, FramesTotal = 1, Sample, Samples;
    public double Progress = -1;      // 0..1, or -1 while not known yet
    public double Elapsed, Eta = -1;  // seconds
    public double Stamp;              // unix seconds of the last write

    public bool Rendering { get { return State == "rendering"; } }
}

class RenderWatch
{
    readonly string path;
    readonly DispatcherTimer timer;
    DateTime lastWrite = DateTime.MinValue;
    string lastState = "idle";
    public RenderStatus Current = new RenderStatus();
    public event Action<RenderStatus> Changed;

    public RenderWatch(string dataDir)
    {
        path = Path.Combine(dataDir, "render.json");
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += delegate { Check(); };
    }

    public void Stop() { timer.Stop(); }

    public void Start()
    {
        // A file left over from a render that ended while the island was not running is not news.
        try
        {
            if (File.Exists(path))
            {
                lastWrite = File.GetLastWriteTimeUtc(path);
                var s = Read();
                if (s != null && s.Rendering && Age(s) < 30) { Current = s; lastState = s.State; Fire(); }
            }
        }
        catch (Exception) { }
        timer.Start();
    }

    static double Now() { return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }
    static double Age(RenderStatus s) { return Now() - s.Stamp; }

    void Check()
    {
        try
        {
            if (!File.Exists(path))
            {
                if (Current.Rendering) { Current = new RenderStatus { State = "cancelled" }; lastState = "cancelled"; Fire(); }
                return;
            }
            var t = File.GetLastWriteTimeUtc(path);
            if (t == lastWrite)
            {
                // Blender closed or crashed mid-render: no word for a long while means it is over.
                if (Current.Rendering && Age(Current) > 90) { Current.State = "cancelled"; lastState = "cancelled"; Fire(); }
                return;
            }
            lastWrite = t;
            var s = Read();
            if (s == null) return;
            if (!s.Rendering && s.State == lastState) { Current = s; return; }
            Current = s;
            lastState = s.State;
            Fire();
        }
        catch (Exception) { }
    }

    void Fire()
    {
        var h = Changed;
        if (h != null) h(Current);
    }

    RenderStatus Read()
    {
        string text;
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
        }
        catch (Exception) { return null; }
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            var s = new RenderStatus();
            s.State = Str(d, "state", "idle");
            s.App = Str(d, "app", "Blender");
            s.File = Str(d, "file", "");
            s.Animation = Num(d, "animation", 0) > 0;
            s.Frame = (int)Num(d, "frame", 0);
            s.FramesDone = (int)Num(d, "frames_done", 0);
            s.FramesTotal = Math.Max(1, (int)Num(d, "frames_total", 1));
            s.Sample = (int)Num(d, "sample", 0);
            s.Samples = (int)Num(d, "samples", 0);
            s.Progress = Num(d, "progress", -1);
            s.Elapsed = Num(d, "elapsed", 0);
            s.Eta = Num(d, "eta", -1);
            s.Stamp = Num(d, "t", Now());
            return s;
        }
        catch (Exception) { return null; }
    }

    static string Str(Dictionary<string, object> d, string k, string fallback)
    {
        object v;
        return d.TryGetValue(k, out v) && v != null ? v.ToString() : fallback;
    }

    static double Num(Dictionary<string, object> d, string k, double fallback)
    {
        object v;
        if (!d.TryGetValue(k, out v) || v == null) return fallback;
        if (v is bool) return (bool)v ? 1 : 0;
        try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return fallback; }
    }
}
