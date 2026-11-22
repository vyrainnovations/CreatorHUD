// CreatorHUD liquid glass: live capture of the screen behind the island, the glass and tab-lens
// pixel shaders (compiled at start-up with the d3dcompiler_47.dll that ships with Windows, so no SDK
// is needed), and the progress ring used for renders.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// ---------------------------------------------------------------- shader compiler

static class Shaders
{
    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] UIntPtr GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll")]
    static extern int D3DCompile([MarshalAs(UnmanagedType.LPStr)] string src, UIntPtr srcSize, [MarshalAs(UnmanagedType.LPStr)] string name,
        IntPtr defines, IntPtr include, [MarshalAs(UnmanagedType.LPStr)] string entry, [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1, uint flags2, out ID3DBlob code, out ID3DBlob errors);

    // Compiled shaders are cached in the data folder, keyed by a hash of their source, so later starts
    // skip the compiler entirely (it is a sizeable DLL to load).
    public static PixelShader Compile(string hlsl, string name)
    {
        string cache = null;
        try
        {
            string hash;
            using (var sha = System.Security.Cryptography.SHA1.Create())
                hash = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hlsl))).Replace("-", "").Substring(0, 16);
            cache = Path.Combine(Store.Dir, "shader-" + name + "-" + hash + ".cso");
            if (File.Exists(cache))
            {
                var cached = new PixelShader();
                cached.SetStreamSource(new MemoryStream(File.ReadAllBytes(cache)));
                cached.Freeze();
                return cached;
            }
        }
        catch (Exception) { cache = null; }
        try
        {
            ID3DBlob code, errors;
            int hr = D3DCompile(hlsl, new UIntPtr((uint)hlsl.Length), name, IntPtr.Zero, IntPtr.Zero, "main", "ps_3_0", 1u << 15, 0, out code, out errors);
            if (hr < 0 || code == null)
            {
                string msg = errors != null ? Marshal.PtrToStringAnsi(errors.GetBufferPointer()) : ("hr " + hr);
                Store.Log("shader " + name + ": " + msg);
                return null;
            }
            var bytes = new byte[(int)code.GetBufferSize().ToUInt32()];
            Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
            if (cache != null)
            {
                try
                {
                    Directory.CreateDirectory(Store.Dir);
                    foreach (var old in Directory.GetFiles(Store.Dir, "shader-" + name + "-*.cso")) File.Delete(old);
                    File.WriteAllBytes(cache, bytes);
                }
                catch (Exception) { }
            }
            var ps = new PixelShader();
            ps.SetStreamSource(new MemoryStream(bytes));
            ps.Freeze();
            return ps;
        }
        catch (Exception ex)
        {
            Store.Log("shader " + name + ": " + ex.Message);
            return null;
        }
    }

    // Shared by both shaders: signed distance to a rounded rectangle centred on the origin.
    const string Common = @"
float sdRound(float2 p, float2 halfSize, float r)
{
    float2 q = abs(p) - halfSize + r;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
}
float2 sdNormal(float2 p, float2 halfSize, float r)
{
    float dx = sdRound(p + float2(0.5, 0), halfSize, r) - sdRound(p - float2(0.5, 0), halfSize, r);
    float dy = sdRound(p + float2(0, 0.5), halfSize, r) - sdRound(p - float2(0, 0.5), halfSize, r);
    float2 g = float2(dx, dy);
    float l = length(g);
    return l > 0.0001 ? g / l : float2(0, -1);
}
";

    // The island's glass. The element is the island grown by `margin` on every side, filled with the
    // live capture of the screen behind it. Inside the rounded shape the background shows through almost
    // clear: a light blur, a touch more saturation and a light dark tint. Near the edge the glass bends
    // light, pulling in what lies just outside the shape (with a little colour fringing), and a thin
    // specular rim catches the light at the top-left and bottom-right.
    public const string Glass = Common + @"
sampler2D input : register(s0);
float2 size : register(c0);
float margin : register(c1);
float radius : register(c2);
float bezel : register(c3);
float refraction : register(c4);
float blurDip : register(c5);
float tint : register(c6);
float highlight : register(c7);

float3 soft(float2 uv, float2 px)
{
    float3 c = tex2D(input, uv).rgb * 0.25;
    c += (tex2D(input, uv + float2(px.x, 0)).rgb + tex2D(input, uv - float2(px.x, 0)).rgb
        + tex2D(input, uv + float2(0, px.y)).rgb + tex2D(input, uv - float2(0, px.y)).rgb) * 0.125;
    c += (tex2D(input, uv + px).rgb + tex2D(input, uv - px).rgb
        + tex2D(input, uv + float2(px.x, -px.y)).rgb + tex2D(input, uv + float2(-px.x, px.y)).rgb) * 0.0625;
    return c;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = uv * size;
    float2 halfSize = size * 0.5 - margin;
    float2 lp = p - size * 0.5;
    float r = min(radius, min(halfSize.x, halfSize.y));
    float d = sdRound(lp, halfSize, r);
    float e = max(-d, 0);
    float2 n = sdNormal(lp, halfSize, r);

    float bz = max(0.5, min(bezel, min(halfSize.x, halfSize.y)));
    float t = saturate(1 - e / bz);
    float bend = refraction * t * t;
    float2 du = 1 / size;
    // The capture arrives already blurred (LiveBackdrop), so one sample is enough.
    float3 col = tex2D(input, uv + n * bend * du).rgb;

    float l = dot(col, float3(0.299, 0.587, 0.114));
    col = lerp(float3(l, l, l), col, 1.18);
    col *= 1 - tint;

    float2 light = normalize(float2(-0.55, -0.85));
    float facing = abs(dot(n, light));
    float rim = saturate(1 - e / 1.1);
    col += highlight * (rim * (0.30 + 0.70 * facing) + t * t * t * 0.09 * (0.35 + 0.65 * facing));

    float a = saturate(0.5 - d);
    return float4(saturate(col) * a, a);
}
";

    // The tab capsule. Applied to the whole tab track: outside the capsule the track is drawn as it is;
    // inside, the icons under it are magnified and bent toward the rim like a drop of water, with a faint
    // white fill and a bright rim. It stretches with its speed (stretch).
    public const string TabLens = Common + @"
sampler2D input : register(s0);
float2 size : register(c0);
float capX : register(c1);
float capY : register(c2);
float capW : register(c3);
float capH : register(c4);
float stretch : register(c5);
float strength : register(c6);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = uv * size;
    float4 under = tex2D(input, uv);
    float w = capW * (1 + 0.20 * stretch);
    float h = capH * (1 - 0.07 * stretch);
    float2 c = float2(capX + capW * 0.5, capY + capH * 0.5);
    float2 lp = p - c;
    float2 halfSize = float2(w, h) * 0.5;
    float r = halfSize.y;
    float d = sdRound(lp, halfSize, r);
    float e = max(-d, 0);
    float2 n = sdNormal(lp, halfSize, r);

    float bz = min(halfSize.y, 13);
    float t = saturate(1 - e / bz);
    float2 q = c + lp / (1 + 0.09 * strength);
    float2 sp = q - n * (strength * 3.2 * t * t * t);
    float4 lens = tex2D(input, sp / size);

    float fillA = 0.085 + 0.05 * stretch + t * t * 0.06;
    float4 col = lens + float4(fillA, fillA, fillA, fillA) * (1 - lens.a);

    float2 light = normalize(float2(-0.5, -0.86));
    float facing = abs(dot(n, light));
    float rim = saturate(1 - e / 1.15);
    float hl = rim * (0.28 + 0.62 * facing) * 0.75 + t * t * t * 0.08;
    col.rgb += hl;

    col.a = saturate(max(col.a, col.a + hl * 0.5));

    float a = saturate(0.5 - d);
    return lerp(under, saturate(col), a);
}
";
}

// ---------------------------------------------------------------- effects

class LiquidGlassEffect : ShaderEffect
{
    static readonly PixelShader shader = Shaders.Compile(Shaders.Glass, "glass");
    public static bool Available { get { return shader != null; } }

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(LiquidGlassEffect), 0);
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register("Size", typeof(Point), typeof(LiquidGlassEffect), new UIPropertyMetadata(new Point(100, 40), PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty MarginProperty = DependencyProperty.Register("Margin", typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register("Radius", typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(17.0, PixelShaderConstantCallback(2)));
    public static readonly DependencyProperty BezelProperty = DependencyProperty.Register("Bezel", typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(14.0, PixelShaderConstantCallback(3)));
    public static readonly DependencyProperty RefractionProperty = DependencyProperty.Register("Refraction", typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(10.0, PixelShaderConstantCallback(4)));
    public static readonly DependencyProperty BlurProperty = DependencyProperty.Register("Blur", typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(2.5, PixelShaderConstantCallback(5)));
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register("Tint", typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(0.15, PixelShaderConstantCallback(6)));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register("Highlight", typeof(double), typeof(LiquidGlassEffect), new UIPropertyMetadata(0.55, PixelShaderConstantCallback(7)));

    public LiquidGlassEffect()
    {
        PixelShader = shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SizeProperty);
        UpdateShaderValue(MarginProperty);
        UpdateShaderValue(RadiusProperty);
        UpdateShaderValue(BezelProperty);
        UpdateShaderValue(RefractionProperty);
        UpdateShaderValue(BlurProperty);
        UpdateShaderValue(TintProperty);
        UpdateShaderValue(HighlightProperty);
    }

    public Brush Input { get { return (Brush)GetValue(InputProperty); } set { SetValue(InputProperty, value); } }
    public Point Size { get { return (Point)GetValue(SizeProperty); } set { SetValue(SizeProperty, value); } }
    public double Margin { get { return (double)GetValue(MarginProperty); } set { SetValue(MarginProperty, value); } }
    public double Radius { get { return (double)GetValue(RadiusProperty); } set { SetValue(RadiusProperty, value); } }
    public double Bezel { get { return (double)GetValue(BezelProperty); } set { SetValue(BezelProperty, value); } }
    public double Refraction { get { return (double)GetValue(RefractionProperty); } set { SetValue(RefractionProperty, value); } }
    public double Blur { get { return (double)GetValue(BlurProperty); } set { SetValue(BlurProperty, value); } }
    public double Tint { get { return (double)GetValue(TintProperty); } set { SetValue(TintProperty, value); } }
    public double Highlight { get { return (double)GetValue(HighlightProperty); } set { SetValue(HighlightProperty, value); } }
}

class TabLensEffect : ShaderEffect
{
    static readonly PixelShader shader = Shaders.Compile(Shaders.TabLens, "tablens");
    public static bool Available { get { return shader != null; } }

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(TabLensEffect), 0);
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register("Size", typeof(Point), typeof(TabLensEffect), new UIPropertyMetadata(new Point(100, 40), PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty CapXProperty = DependencyProperty.Register("CapX", typeof(double), typeof(TabLensEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty CapYProperty = DependencyProperty.Register("CapY", typeof(double), typeof(TabLensEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(2)));
    public static readonly DependencyProperty CapWProperty = DependencyProperty.Register("CapW", typeof(double), typeof(TabLensEffect), new UIPropertyMetadata(60.0, PixelShaderConstantCallback(3)));
    public static readonly DependencyProperty CapHProperty = DependencyProperty.Register("CapH", typeof(double), typeof(TabLensEffect), new UIPropertyMetadata(40.0, PixelShaderConstantCallback(4)));
    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register("Stretch", typeof(double), typeof(TabLensEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(5)));
    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register("Strength", typeof(double), typeof(TabLensEffect), new UIPropertyMetadata(1.0, PixelShaderConstantCallback(6)));

    public TabLensEffect()
    {
        PixelShader = shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SizeProperty);
        UpdateShaderValue(CapXProperty);
        UpdateShaderValue(CapYProperty);
        UpdateShaderValue(CapWProperty);
        UpdateShaderValue(CapHProperty);
        UpdateShaderValue(StretchProperty);
        UpdateShaderValue(StrengthProperty);
    }

    public Brush Input { get { return (Brush)GetValue(InputProperty); } set { SetValue(InputProperty, value); } }
    public Point Size { get { return (Point)GetValue(SizeProperty); } set { SetValue(SizeProperty, value); } }
    public double CapX { get { return (double)GetValue(CapXProperty); } set { SetValue(CapXProperty, value); } }
    public double CapY { get { return (double)GetValue(CapYProperty); } set { SetValue(CapYProperty, value); } }
    public double CapW { get { return (double)GetValue(CapWProperty); } set { SetValue(CapWProperty, value); } }
    public double CapH { get { return (double)GetValue(CapHProperty); } set { SetValue(CapHProperty, value); } }
    public double Stretch { get { return (double)GetValue(StretchProperty); } set { SetValue(StretchProperty, value); } }
    public double Strength { get { return (double)GetValue(StrengthProperty); } set { SetValue(StrengthProperty, value); } }
}

// ---------------------------------------------------------------- live capture

// Copies the strip of screen behind the island into a small bitmap, many times a second, on its own
// thread. Each copy waits for the screen's next refresh, so it costs time, not CPU. The island hides
// itself from capture (see Island.SetRecordable), so the copy shows what is really behind it.
class LiveBackdrop : IDisposable
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] static extern void CopyMemory(IntPtr dst, IntPtr src, UIntPtr len);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    readonly Dispatcher dispatcher;
    readonly object gate = new object();
    readonly IntPtr memDc, dib, bits;
    readonly IntPtr oldBmp;
    readonly int srcX, srcY, srcW, srcH, dstW, dstH;
    readonly Thread thread;
    volatile bool stop, paused = true, uiPending, forcePresent = true;
    volatile int rows;          // how many rows of the strip to capture (the island's current depth)
    uint lastPrint;
    volatile int interval = 50;
    double meterU0 = 0.3, meterV0 = 0, meterU1 = 0.7, meterV1 = 0.2;
    double luminance = 0.2;

    public readonly WriteableBitmap Bitmap;
    public readonly Rect AreaDip;

    // Shown to recordings, the capture sees the island itself. Then each frame cuts the island (and
    // the render bubble) out and refills the hole from the live background around it (push-pull
    // fill), so the glass still shows what is really behind at its edges and a soft blend inside.
    volatile bool fillSelf;

    // The frosting is done here, on this thread, at low resolution: a three-pass box blur (close to a
    // Gaussian). It used to be a GPU blur that WPF re-ran on every redraw of the island.
    volatile float blurTarget;
    float blurNow = -1;
    byte[] outBuf;
    int[] line;

    public void SetBlur(double dip) { blurTarget = (float)Math.Max(0, dip * dstW / AreaDip.Width); }

    void BlurInto(int r)
    {
        int n = dstW * dstH * 4;
        if (outBuf == null || outBuf.Length != n) outBuf = new byte[n];
        if (line == null || line.Length < Math.Max(dstW, dstH)) line = new int[Math.Max(dstW, dstH)];
        Marshal.Copy(bits, outBuf, 0, dstW * r * 4);
        int b = (int)Math.Round(blurNow / 2.2f);
        if (b < 1) return;
        for (int pass = 0; pass < 3; pass++) { BoxH(outBuf, dstW, r, b, line); BoxV(outBuf, dstW, r, b, line); }
    }

    static void BoxH(byte[] p, int w, int h, int b, int[] line)
    {
        int div = 2 * b + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -b; k <= b; k++) sum += p[row + Math.Min(w - 1, Math.Max(0, k)) * 4 + c];
                for (int x = 0; x < w; x++)
                {
                    line[x] = sum / div;
                    sum += p[row + Math.Min(w - 1, x + b + 1) * 4 + c] - p[row + Math.Max(0, x - b) * 4 + c];
                }
                for (int x = 0; x < w; x++) p[row + x * 4 + c] = (byte)line[x];
            }
        }
    }

    static void BoxV(byte[] p, int w, int h, int b, int[] line)
    {
        int div = 2 * b + 1, stride = w * 4;
        for (int x = 0; x < w; x++)
        {
            int col = x * 4;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -b; k <= b; k++) sum += p[Math.Min(h - 1, Math.Max(0, k)) * stride + col + c];
                for (int y = 0; y < h; y++)
                {
                    line[y] = sum / div;
                    sum += p[Math.Min(h - 1, y + b + 1) * stride + col + c] - p[Math.Max(0, y - b) * stride + col + c];
                }
                for (int y = 0; y < h; y++) p[y * stride + col + c] = (byte)line[y];
            }
        }
    }
    double mx, my, mw, mh, mr, bx, by, br;    // masks in capture pixels
    byte[] buf;
    float[][] lc, lw;
    int[] lW, lH;

    public void SetFillSelf(bool on) { fillSelf = on; }

    // Island rounded rect and bubble circle, in WPF units relative to AreaDip (br = 0: no bubble).
    public void SetMask(double x, double y, double w, double h, double r, double bcx, double bcy, double brad)
    {
        double k = dstW / AreaDip.Width;
        mx = x * k; my = y * k; mw = w * k; mh = h * k; mr = r * k;
        bx = bcx * k; by = bcy * k; br = brad * k;
    }
    public event Action FrameArrived;
    public double Luminance { get { return luminance; } }

    // area: the strip to watch, in WPF units; scale: screen pixels per WPF unit; shrink: 2 = half size.
    public LiveBackdrop(Dispatcher dispatcher, Rect area, double scale, int shrink)
    {
        this.dispatcher = dispatcher;
        AreaDip = area;
        srcX = (int)Math.Round(area.X * scale);
        srcY = (int)Math.Round(area.Y * scale);
        srcW = Math.Max(16, (int)Math.Round(area.Width * scale));
        srcH = Math.Max(16, (int)Math.Round(area.Height * scale));
        dstW = Math.Max(8, srcW / shrink);
        dstH = Math.Max(8, srcH / shrink);

        IntPtr screen = GetDC(IntPtr.Zero);
        memDc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = dstW, biHeight = -dstH, biPlanes = 1, biBitCount = 32 };
        dib = CreateDIBSection(memDc, ref bi, 0, out bits, IntPtr.Zero, 0);
        oldBmp = SelectObject(memDc, dib);
        SetStretchBltMode(memDc, 4);    // HALFTONE: averages pixels as it shrinks, a free first blur
        SetBrushOrgEx(memDc, 0, 0, IntPtr.Zero);

        Bitmap = new WriteableBitmap(dstW, dstH, 96, 96, PixelFormats.Bgr32, null);
        rows = dstH;
        thread = new Thread(Loop) { IsBackground = true, Name = "CreatorHUD glass", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    // Frames per second for the current state; 0 pauses (the last frame stays on the glass).
    public void SetRate(int fps)
    {
        if (fps <= 0) { paused = true; return; }
        interval = 1000 / fps;
        paused = false;
    }

    // The part of the strip (0..1 on each axis) whose brightness sets the glass tint.
    // The depth of strip to capture, in WPF units from its top: a pill needs far less than the open panel.
    public void SetDepth(double dip)
    {
        int r = Math.Max(8, Math.Min(dstH, (int)Math.Ceiling(dip * dstH / AreaDip.Height) + 2));
        if (r > rows) forcePresent = true;
        rows = r;
    }

    public void SetMeter(double u0, double v0, double u1, double v1)
    {
        meterU0 = Math.Max(0, u0); meterV0 = Math.Max(0, v0); meterU1 = Math.Min(1, u1); meterV1 = Math.Min(1, v1);
    }

    public void CaptureNow()
    {
        forcePresent = true;
        Grab();
        Present();
    }

    // Stops the live updates and takes one last frame after any capture in flight has finished, so
    // the frozen glass can never contain the island itself.
    public void Freeze()
    {
        paused = true;
        lock (gate) { }
        forcePresent = true;
        Grab();
        Present();
    }

    void Loop()
    {
        var sw = new System.Diagnostics.Stopwatch();
        while (!stop)
        {
            if (paused) { Thread.Sleep(60); continue; }
            sw.Restart();
            try
            {
                // Most of the time the screen behind is still: then nothing is handed to the UI, and the
                // island isn't redrawn at all.
                if (Grab() && !uiPending)
                {
                    uiPending = true;
                    dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Present));
                }
            }
            catch (Exception) { }
            int rest = interval - (int)sw.ElapsedMilliseconds;
            if (rest > 0) Thread.Sleep(rest);
        }
    }

    // Returns whether the frame differs from the last one.
    bool Grab()
    {
        lock (gate)
        {
            int r = rows;
            int sh = (int)Math.Round((double)srcH * r / dstH);
            IntPtr screen = GetDC(IntPtr.Zero);
            StretchBlt(memDc, 0, 0, dstW, r, screen, srcX, srcY, srcW, sh, 0x00CC0020);
            ReleaseDC(IntPtr.Zero, screen);
            if (fillSelf) try { FillSelf(); } catch (Exception) { }
            uint print = Fingerprint(r);
            bool blurMoving = Math.Abs(blurNow - blurTarget) > 0.05f;
            bool changed = forcePresent || print != lastPrint || blurMoving;
            lastPrint = print;
            forcePresent = false;
            if (changed)
            {
                luminance = Meter();
                if (blurMoving) blurNow = Math.Abs(blurNow - blurTarget) < 0.3f || blurNow < 0 ? blurTarget : blurNow + (blurTarget - blurNow) * 0.35f;
                BlurInto(r);
            }
            return changed;
        }
    }

    // A cheap fingerprint over a sparse grid of the captured rows (FNV-1a).
    uint Fingerprint(int r)
    {
        uint h = 2166136261;
        int stepX = Math.Max(1, dstW / 48), stepY = Math.Max(1, r / 16);
        for (int y = 0; y < r; y += stepY)
            for (int x = 0; x < dstW; x += stepX)
            {
                h ^= (uint)Marshal.ReadInt32(bits, (y * dstW + x) * 4);
                h *= 16777619;
            }
        return h;
    }

    bool Masked(int x, int y)
    {
        double px = x + 0.5, py = y + 0.5;
        if (br > 0) { double dx = px - bx, dy = py - by; if (dx * dx + dy * dy <= br * br) return true; }
        if (px < mx || py < my || px > mx + mw || py > my + mh) return false;
        double r = Math.Min(mr, Math.Min(mw, mh) / 2);
        double cx = Math.Max(mx + r, Math.Min(mx + mw - r, px)), cy = Math.Max(my + r, Math.Min(my + mh - r, py));
        double ex = px - cx, ey = py - cy;
        return ex * ex + ey * ey <= r * r;
    }

    void FillSelf()
    {
        if (mw <= 0 || mh <= 0) return;
        int n = dstW * dstH * 4;
        if (buf == null || buf.Length != n) buf = new byte[n];
        Marshal.Copy(bits, buf, 0, n);
        const int S = 4;
        int w0 = (dstW + S - 1) / S, h0 = (dstH + S - 1) / S;
        if (lc == null || lW[0] != w0 || lH[0] != h0)
        {
            int levels = 1;
            for (int a = w0, b = h0; a > 1 || b > 1; a = (a + 1) / 2, b = (b + 1) / 2) levels++;
            lc = new float[levels][]; lw = new float[levels][]; lW = new int[levels]; lH = new int[levels];
            for (int l = 0, a = w0, b = h0; l < levels; l++, a = Math.Max(1, (a + 1) / 2), b = Math.Max(1, (b + 1) / 2))
            {
                lW[l] = a; lH[l] = b; lc[l] = new float[a * b * 3]; lw[l] = new float[a * b];
            }
        }
        // level 0: average of the unmasked pixels in each 4x4 cell
        Array.Clear(lc[0], 0, lc[0].Length);
        Array.Clear(lw[0], 0, lw[0].Length);
        for (int y = 0; y < dstH; y++)
            for (int x = 0; x < dstW; x++)
            {
                if (Masked(x, y)) continue;
                int i = (y * dstW + x) * 4, c = (y / S) * w0 + x / S;
                lc[0][c * 3] += buf[i]; lc[0][c * 3 + 1] += buf[i + 1]; lc[0][c * 3 + 2] += buf[i + 2];
                lw[0][c] += 1;
            }
        for (int c = 0; c < w0 * h0; c++)
            if (lw[0][c] > 0) { float k = 1 / lw[0][c]; lc[0][c * 3] *= k; lc[0][c * 3 + 1] *= k; lc[0][c * 3 + 2] *= k; lw[0][c] = Math.Min(1, lw[0][c] / (S * S) * 2); }
        // push: weighted averages up the pyramid
        for (int l = 1; l < lc.Length; l++)
        {
            int W = lW[l], H = lH[l], cw = lW[l - 1], ch = lH[l - 1];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float r = 0, g = 0, b = 0, ws = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Math.Min(cw - 1, x * 2 + dx), sy = Math.Min(ch - 1, y * 2 + dy), si = sy * cw + sx;
                            float ww = lw[l - 1][si];
                            r += lc[l - 1][si * 3] * ww; g += lc[l - 1][si * 3 + 1] * ww; b += lc[l - 1][si * 3 + 2] * ww; ws += ww;
                        }
                    int o = y * W + x;
                    if (ws > 0) { lc[l][o * 3] = r / ws; lc[l][o * 3 + 1] = g / ws; lc[l][o * 3 + 2] = b / ws; }
                    lw[l][o] = Math.Min(1, ws);
                }
        }
        // pull: fill thin cells from the coarser level, back down to level 0
        for (int l = lc.Length - 2; l >= 0; l--)
        {
            int W = lW[l], H = lH[l], pw = lW[l + 1];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int o = y * W + x, p = Math.Min(lH[l + 1] - 1, y / 2) * pw + Math.Min(pw - 1, x / 2);
                    float ww = lw[l][o];
                    if (ww >= 1) continue;
                    for (int ch = 0; ch < 3; ch++) lc[l][o * 3 + ch] = lc[l][o * 3 + ch] * ww + lc[l + 1][p * 3 + ch] * (1 - ww);
                    lw[l][o] = 1;
                }
        }
        // write the filled colour into the masked pixels, smoothly interpolated
        float[] f = lc[0];
        for (int y = 0; y < dstH; y++)
            for (int x = 0; x < dstW; x++)
            {
                if (!Masked(x, y)) continue;
                float gx = Math.Max(0, Math.Min(w0 - 1, (x + 0.5f) / S - 0.5f)), gy = Math.Max(0, Math.Min(h0 - 1, (y + 0.5f) / S - 0.5f));
                int x0 = (int)gx, y0 = (int)gy, x1 = Math.Min(w0 - 1, x0 + 1), y1 = Math.Min(h0 - 1, y0 + 1);
                float tx = gx - x0, ty = gy - y0;
                int i = (y * dstW + x) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    float a = f[(y0 * w0 + x0) * 3 + ch] * (1 - tx) + f[(y0 * w0 + x1) * 3 + ch] * tx;
                    float b = f[(y1 * w0 + x0) * 3 + ch] * (1 - tx) + f[(y1 * w0 + x1) * 3 + ch] * tx;
                    buf[i + ch] = (byte)Math.Max(0, Math.Min(255, a * (1 - ty) + b * ty));
                }
            }
        Marshal.Copy(buf, 0, bits, n);
    }

    // Average brightness over a sparse grid of the metered area.
    double Meter()
    {
        int x0 = (int)(meterU0 * dstW), x1 = Math.Max(x0 + 1, (int)(meterU1 * dstW)), y0 = (int)(meterV0 * dstH), y1 = Math.Max(y0 + 1, (int)(meterV1 * dstH));
        double sum = 0;
        int n = 0;
        int stepX = Math.Max(1, (x1 - x0) / 16), stepY = Math.Max(1, (y1 - y0) / 6);
        for (int y = y0; y < y1 && y < dstH; y += stepY)
            for (int x = x0; x < x1 && x < dstW; x += stepX)
            {
                int v = Marshal.ReadInt32(bits, (y * dstW + x) * 4);
                double b = v & 0xFF, g = (v >> 8) & 0xFF, r = (v >> 16) & 0xFF;
                sum += (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                n++;
            }
        return n == 0 ? 0.2 : sum / n;
    }

    void Present()
    {
        uiPending = false;
        if (stop) return;
        lock (gate)
        {
            Bitmap.Lock();
            if (outBuf != null) Marshal.Copy(outBuf, 0, Bitmap.BackBuffer, dstW * dstH * 4);
            else CopyMemory(Bitmap.BackBuffer, bits, new UIntPtr((uint)(dstW * dstH * 4)));
            Bitmap.AddDirtyRect(new Int32Rect(0, 0, dstW, dstH));
            Bitmap.Unlock();
        }
        var h = FrameArrived;
        if (h != null) h();
    }

    public void Dispose()
    {
        stop = true;
        try { thread.Join(500); } catch (Exception) { }
        lock (gate)
        {
            SelectObject(memDc, oldBmp);
            DeleteObject(dib);
            DeleteDC(memDc);
        }
    }
}

// ---------------------------------------------------------------- progress ring

class RingView : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(RingView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register("Fill", typeof(Brush), typeof(RingView), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get { return (double)GetValue(ProgressProperty); } set { SetValue(ProgressProperty, value); } }
    public Brush Fill { get { return (Brush)GetValue(FillProperty); } set { SetValue(FillProperty, value); } }
    public double Thickness = 3;
    public bool Indeterminate;
    static readonly Brush Track = MakeTrack();

    static Brush MakeTrack() { var b = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)); b.Freeze(); return b; }

    readonly RotateTransform spin = new RotateTransform();

    public RingView()
    {
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = spin;
        IsHitTestVisible = false;
    }

    public void SetIndeterminate(bool on)
    {
        if (on == Indeterminate) return;
        Indeterminate = on;
        if (on)
        {
            var a = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(a, 60);
            spin.BeginAnimation(RotateTransform.AngleProperty, a);
        }
        else
        {
            spin.BeginAnimation(RotateTransform.AngleProperty, null);
            spin.Angle = 0;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = s / 2 - Thickness / 2;
        dc.DrawEllipse(null, new Pen(Track, Thickness), c, r, r);
        double p = Indeterminate ? 0.28 : Math.Max(0, Math.Min(1, Progress));
        if (p <= 0.002) return;
        var pen = new Pen(Fill, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (p >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a = p * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        dc.DrawGeometry(null, pen, geo);
    }
}

// ---------------------------------------------------------------- jelly sliders

// Makes a slider feel like jelly: pressing squishes it a little, dragging fast leans the leading edge
// forward, pulling past either end stretches it like rubber, and letting go wobbles it back on a
// spring. Pure transforms, so the slider's own logic is untouched.
class Jelly
{
    readonly FrameworkElement el;
    readonly bool vertical;
    readonly ScaleTransform scale = new ScaleTransform(1, 1);
    readonly TranslateTransform shift = new TranslateTransform();
    double lead, lastPos;
    TimeSpan lastAt;
    bool active;
    Point mouse;

    public Jelly(FrameworkElement el, bool vertical)
    {
        this.el = el;
        this.vertical = vertical;
        var g = new TransformGroup();
        g.Children.Add(scale);
        g.Children.Add(shift);
        el.RenderTransform = g;
        // The sliders mark these events handled, so listen to handled ones too.
        el.AddHandler(UIElement.MouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler((s, e) => Begin(e.GetPosition(el))), true);
        el.AddHandler(UIElement.MouseWheelEvent, new System.Windows.Input.MouseWheelEventHandler((s, e) => Poke()), true);
        el.MouseMove += (s, e) => { if (active) mouse = e.GetPosition(el); };
        el.LostMouseCapture += (s, e) => End();
    }

    double Axis(Point p) { return vertical ? p.Y : p.X; }

    void Hold(DependencyObject o, DependencyProperty p)
    {
        var v = (double)o.GetValue(p);
        ((Animatable)o).BeginAnimation(p, null);
        o.SetValue(p, v);
    }

    void Begin(Point p)
    {
        if (active) return;
        active = true;
        mouse = p;
        lastPos = Axis(p);
        lastAt = TimeSpan.Zero;
        lead = 0;
        Hold(scale, ScaleTransform.ScaleXProperty);
        Hold(scale, ScaleTransform.ScaleYProperty);
        Hold(shift, TranslateTransform.XProperty);
        Hold(shift, TranslateTransform.YProperty);
        CompositionTarget.Rendering += OnFrame;
    }

    static double Rubber(double over) { return over <= 0 ? 0 : 13 * (1 - Math.Exp(-over / 50)); }

    void OnFrame(object sender, EventArgs e)
    {
        double size = vertical ? el.ActualHeight : el.ActualWidth, across = vertical ? el.ActualWidth : el.ActualHeight;
        if (size <= 0) return;
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == lastAt) return;
        double pos = Axis(mouse), dt = lastAt == TimeSpan.Zero ? 1.0 / 60 : (now - lastAt).TotalSeconds;
        double v = dt > 0.001 ? (pos - lastPos) / dt : 0;
        lastPos = pos;
        lastAt = now;
        double target = Math.Max(-6, Math.Min(6, v * 0.004));
        lead += (target - lead) * 0.25;

        double squeeze = -size * 0.012;
        double startOff = squeeze + Rubber(-pos) + (lead < 0 ? -lead : 0);
        double endOff = squeeze + Rubber(pos - size) + (lead > 0 ? lead : 0);
        double stretch = (startOff + endOff - 2 * squeeze) / size;
        double bulge = 1.045 - stretch * 0.6;
        double along = (size + startOff + endOff) / size;
        if (vertical)
        {
            scale.CenterX = across / 2; scale.CenterY = 0;
            scale.ScaleX = bulge; scale.ScaleY = along;
            shift.X = 0; shift.Y = -startOff;
        }
        else
        {
            scale.CenterY = across / 2; scale.CenterX = 0;
            scale.ScaleY = bulge; scale.ScaleX = along;
            shift.Y = 0; shift.X = -startOff;
        }
    }

    static DoubleAnimation Wobble(double to, double from, double ms)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Spring.Bouncy };
        if (!double.IsNaN(from)) a.From = from;
        Timeline.SetDesiredFrameRate(a, 60);
        return a;
    }

    void End()
    {
        if (!active) return;
        active = false;
        CompositionTarget.Rendering -= OnFrame;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Wobble(1, double.NaN, 700));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Wobble(1, double.NaN, 700));
        shift.BeginAnimation(TranslateTransform.XProperty, Wobble(0, double.NaN, 700));
        shift.BeginAnimation(TranslateTransform.YProperty, Wobble(0, double.NaN, 700));
    }

    // A small wobble for scroll-wheel steps.
    void Poke()
    {
        if (active) return;
        double across = vertical ? el.ActualWidth : el.ActualHeight;
        scale.CenterX = vertical ? across / 2 : 0;
        scale.CenterY = vertical ? 0 : across / 2;
        if (vertical) scale.BeginAnimation(ScaleTransform.ScaleXProperty, Wobble(1, 1.035, 520));
        else scale.BeginAnimation(ScaleTransform.ScaleYProperty, Wobble(1, 1.06, 520));
    }
}

// ---------------------------------------------------------------- frame timing (diagnostics)

// Only when CreatorHUD is started with CREATORHUD_PERF=1: logs, once a second, how many frames were
// drawn, the slowest frame and how many missed a refresh, to perf.log in the data folder.
static class Perf
{
    public static readonly bool On = Environment.GetEnvironmentVariable("CREATORHUD_PERF") == "1";
    // Experiment switches for finding costs: CREATORHUD_OFF=NOBLUR,NOGLASS,NOSHADOW,NOLENS,NOCAPTURE
    static readonly string off = (Environment.GetEnvironmentVariable("CREATORHUD_OFF") ?? "").ToUpperInvariant();
    public static bool Off(string what) { return off.Contains(what); }
    static TimeSpan last = TimeSpan.Zero;
    static int frames, slow;
    static double worst;
    static DateTime windowStart = DateTime.Now;
    public static Func<string> State = () => "";

    public static void Start()
    {
        if (!On) return;
        CompositionTarget.Rendering += (s, e) =>
        {
            var t = ((RenderingEventArgs)e).RenderingTime;
            if (t == last) return;    // the same frame reported twice
            if (last != TimeSpan.Zero)
            {
                double ms = (t - last).TotalMilliseconds;
                frames++;
                if (ms > worst) worst = ms;
                if (ms > 22) slow++;
            }
            last = t;
            if ((DateTime.Now - windowStart).TotalSeconds >= 1)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Store.Dir, "perf.log"),
                        string.Format("{0:HH:mm:ss.f} fps {1} worst {2:F1}ms missed {3} {4}\r\n", DateTime.Now, frames, worst, slow, State()));
                }
                catch (Exception) { }
                frames = 0; slow = 0; worst = 0;
                windowStart = DateTime.Now;
            }
        };
    }
}

// ---------------------------------------------------------------- app icons for notifications

static class AppIcons
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap); }

    [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref BITMAP bmp);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

    // The icon the Start menu shows for this app ID (works for Store apps and ordinary programs alike).
    public static BitmapSource FromAppId(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            IShellItemImageFactory f;
            if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, ref iid, out f) != 0 || f == null) return null;
            if (f.GetImage(new NativeSize { cx = 96, cy = 96 }, 0x1 | 0x4, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;    // bigger size ok, icon only
            var bm = new BITMAP();
            GetObject(hbmp, Marshal.SizeOf(typeof(BITMAP)), ref bm);
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0) return null;
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            GetDIBits(dc, hbmp, 0, (uint)h, px, ref bi, 0);
            ReleaseDC(IntPtr.Zero, dc);
            bool anyAlpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
            src.Freeze();
            return src;
        }
        catch (Exception) { return null; }
        finally { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); }
    }

    // Last resort: the icon of a running program whose name matches the app's.
    public static BitmapSource FromProcess(string appName)
    {
        if (string.IsNullOrEmpty(appName)) return null;
        try
        {
            string key = appName.Replace(" ", "");
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                if (!p.ProcessName.Replace(" ", "").Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                string path;
                try { path = p.MainModule.FileName; } catch (Exception) { continue; }
                using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (ico == null) continue;
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }
        }
        catch (Exception) { }
        return null;
    }
}
