// BrightLock: tray slider for monitor brightness over DDC/CI that holds the value you pick.
// Built with the C# compiler that ships with Windows (see build.ps1), so it stays C# 5.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BrightLock
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--make-icon") { IconArt.WriteIco(args[1]); return; }

            bool created;
            using (var mutex = new Mutex(true, "BrightLock.SingleInstance", out created))
            {
                if (!created)
                {
                    // Already running: ask that copy to open its slider instead.
                    Native.AllowSetForegroundWindow(-1);   // let the running copy come to the front
                    try { using (var ev = EventWaitHandle.OpenExisting("BrightLock.Show")) ev.Set(); } catch { }
                    return;
                }
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App());
            }
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint count, [Out] PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool DestroyPhysicalMonitor(IntPtr h);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint current, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
    }

    // One physical monitor. All DDC/CI traffic for it runs on its own thread, so a slow
    // monitor never blocks the slider, and only the newest slider value is ever sent.
    class Mon
    {
        const byte Brightness = 0x10;

        public readonly string Key;
        public readonly string Name;
        public readonly IntPtr Handle;
        public readonly int Left;

        public volatile int TargetPct = -1;   // -1 until the first read
        public volatile bool Locked = true;
        public volatile int Failures;
        public int PollMs = 2000;

        public event Action<Mon> Adopted;              // value came from the monitor
        public event Action<Mon, int, int> PutBack;    // (found %, restored %)

        volatile uint max = 100;
        volatile bool dirty, forcePoll, stopping;
        int version;
        int putBackStreak;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread thread;

        public Mon(string key, string name, IntPtr handle, int left)
        {
            Key = key; Name = name; Handle = handle; Left = left;
        }

        public void Start()
        {
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "ddc " + Name;
            thread.Start();
        }

        public void SetPct(int pct)
        {
            TargetPct = Math.Max(0, Math.Min(100, pct));
            Interlocked.Increment(ref version);
            dirty = true;
            wake.Set();
        }

        public void PollNow() { forcePoll = true; wake.Set(); }

        public void Stop()
        {
            stopping = true;
            wake.Set();
            if (thread != null) thread.Join(3000);
            Native.DestroyPhysicalMonitor(Handle);
        }

        void Loop()
        {
            DateTime nextPoll = DateTime.UtcNow;
            while (!stopping)
            {
                if (dirty)
                {
                    dirty = false;
                    int pct = TargetPct;
                    if (pct >= 0) Write(pct);
                    nextPoll = DateTime.UtcNow.AddMilliseconds(PollMs);
                    continue;
                }
                DateTime now = DateTime.UtcNow;
                if (forcePoll || now >= nextPoll)
                {
                    forcePoll = false;
                    Poll();
                    // A monitor that ignores our value (HDR, some picture modes) gets checked less often.
                    nextPoll = DateTime.UtcNow.AddMilliseconds(putBackStreak >= 3 ? 15000 : PollMs);
                    continue;
                }
                wake.WaitOne(nextPoll - now);
            }
        }

        uint ToRaw(int pct) { return (uint)Math.Round(pct * (double)max / 100.0); }
        int ToPct(uint raw) { return (int)Math.Round(raw * 100.0 / max); }

        bool Write(int pct)
        {
            bool ok = Native.SetVCPFeature(Handle, Brightness, ToRaw(pct));
            Failures = ok ? 0 : Failures + 1;
            return ok;
        }

        void Poll()
        {
            int v0 = Thread.VolatileRead(ref version);
            uint cur, mx;
            if (!Native.GetVCPFeatureAndVCPFeatureReply(Handle, Brightness, IntPtr.Zero, out cur, out mx) || mx == 0)
            {
                Failures++;   // usually the monitor is asleep
                return;
            }
            Failures = 0;
            max = mx;
            if (dirty || Thread.VolatileRead(ref version) != v0) return;   // the slider moved meanwhile

            int target = TargetPct;
            int curPct = ToPct(cur);
            if (target < 0) { TargetPct = curPct; Raise(Adopted); return; }
            if (cur == ToRaw(target)) { putBackStreak = 0; return; }

            if (Locked)
            {
                if (Write(target))
                {
                    putBackStreak++;
                    var h = PutBack;
                    if (h != null && putBackStreak == 1) h(this, curPct, target);
                }
            }
            else
            {
                TargetPct = curPct;
                Raise(Adopted);
            }
        }

        void Raise(Action<Mon> h) { if (h != null) h(this); }
    }

    static class Displays
    {
        public static List<Mon> Enumerate()
        {
            var names = FriendlyNames();
            var hmons = new List<IntPtr>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, a, b, c) => { hmons.Add(h); return true; }, IntPtr.Zero);

            var list = new List<Mon>();
            int n = 0;
            foreach (IntPtr hm in hmons)
            {
                var mi = new Native.MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX));
                if (!Native.GetMonitorInfo(hm, ref mi)) continue;

                var ids = new List<string>();
                for (uint i = 0; ; i++)
                {
                    var dd = new Native.DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
                    if (!Native.EnumDisplayDevices(mi.szDevice, i, ref dd, 1)) break;   // 1 = EDD_GET_DEVICE_INTERFACE_NAME
                    if ((dd.StateFlags & 1) != 0) ids.Add(dd.DeviceID);                // 1 = active
                }

                uint count;
                if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out count) || count == 0) continue;
                var arr = new Native.PHYSICAL_MONITOR[count];
                if (!Native.GetPhysicalMonitorsFromHMONITOR(hm, count, arr)) continue;

                for (int k = 0; k < arr.Length; k++)
                {
                    n++;
                    string instance = k < ids.Count ? InstanceFromDeviceId(ids[k]) : null;
                    string name = null;
                    if (instance != null)
                        foreach (var kv in names)
                            if (kv.Key.StartsWith(instance, StringComparison.OrdinalIgnoreCase)) { name = kv.Value; break; }
                    if (string.IsNullOrEmpty(name)) name = "Display " + n;
                    string key = instance ?? (mi.szDevice + "#" + k);
                    list.Add(new Mon(key, name, arr[k].hPhysicalMonitor, mi.rcMonitor.left));
                }
            }

            list.Sort((a, b) => a.Left.CompareTo(b.Left));
            return list;
        }

        // "\\?\DISPLAY#ABC1234#5&1a&0&UID1234#{guid}" -> "DISPLAY\ABC1234\5&1a&0&UID1234"
        public static string InstanceFromDeviceId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string s = id.StartsWith(@"\\?\") ? id.Substring(4) : id;
            var parts = s.Split('#');
            if (parts.Length < 3) return null;
            return parts[0] + "\\" + parts[1] + "\\" + parts[2];
        }

        // Model names from the monitors' EDID, keyed by WMI instance name.
        static Dictionary<string, string> FriendlyNames()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var inst = o["InstanceName"] as string;
                        var chars = o["UserFriendlyName"] as ushort[];
                        if (inst == null || chars == null) continue;
                        var sb = new StringBuilder();
                        foreach (ushort c in chars) if (c != 0) sb.Append((char)c);
                        d[inst] = sb.ToString().Trim();
                    }
            }
            catch { }
            return d;
        }
    }

    class Entry { public int Pct; public bool Locked; }

    class Store
    {
        public readonly Dictionary<string, Entry> Monitors = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        public int PollSeconds = 2;
        public bool FirstRunDone, Linked, Warm;

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrightLock"); }
        }
        static string FilePath { get { return Path.Combine(Dir, "settings.txt"); } }

        public static Store Load()
        {
            var s = new Store();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var p = line.Split('\t');
                    int v;
                    if (p[0] == "poll" && p.Length > 1 && int.TryParse(p[1], out v)) s.PollSeconds = Math.Max(1, Math.Min(60, v));
                    else if (p[0] == "firstrun") s.FirstRunDone = true;
                    else if (p[0] == "linked") s.Linked = p.Length > 1 && p[1] == "1";
                    else if (p[0] == "warm") s.Warm = p.Length > 1 && p[1] == "1";
                    else if (p[0] == "monitor" && p.Length >= 4 && int.TryParse(p[2], out v))
                        s.Monitors[p[1]] = new Entry { Pct = v, Locked = p[3] == "1" };
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.Append("poll\t").Append(PollSeconds).AppendLine();
                if (FirstRunDone) sb.AppendLine("firstrun\t1");
                sb.Append("linked\t").Append(Linked ? "1" : "0").AppendLine();
                sb.Append("warm\t").Append(Warm ? "1" : "0").AppendLine();
                foreach (var kv in Monitors)
                    sb.Append("monitor\t").Append(kv.Key).Append('\t').Append(kv.Value.Pct).Append('\t')
                      .Append(kv.Value.Locked ? "1" : "0").AppendLine();
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            }
            catch { }
        }
    }

    static class Log
    {
        public static string FilePath { get { return Path.Combine(Store.Dir, "log.txt"); } }

        public static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    var lines = File.ReadAllLines(FilePath);
                    File.WriteAllLines(FilePath, lines.Skip(lines.Length / 2));
                }
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    static class Theme
    {
        public static readonly Color Back = Color.FromArgb(32, 32, 32);
        public static readonly Color Text = Color.FromArgb(240, 240, 240);
        public static readonly Color Sub = Color.FromArgb(160, 160, 160);
        public static readonly Color Track = Color.FromArgb(85, 85, 85);
        public static readonly Color Line = Color.FromArgb(58, 58, 58);
        public static readonly Color Accent = Color.FromArgb(76, 194, 255);

        public static bool TaskbarIsLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1;
            }
            catch { return false; }
        }
    }

    static class IconArt
    {
        public static Bitmap Sun(int size, Color color)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float c = size / 2f, core = size * 0.21f, r1 = size * 0.33f, r2 = size * 0.46f;
                using (var b = new SolidBrush(color)) g.FillEllipse(b, c - core, c - core, core * 2, core * 2);
                using (var p = new Pen(color, Math.Max(1.4f, size * 0.085f)))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        float cx = (float)Math.Cos(a), cy = (float)Math.Sin(a);
                        g.DrawLine(p, c + cx * r1, c + cy * r1, c + cx * r2, c + cy * r2);
                    }
                }
            }
            return bmp;
        }

        public static Icon Tray(Color color)
        {
            int s = SystemInformation.SmallIconSize.Width;
            using (var bmp = Sun(s, color)) return Icon.FromHandle(bmp.GetHicon());
        }

        // Writes a multi-size .ico (PNG entries) for the exe.
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 256 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
                using (var bmp = Sun(s, Color.FromArgb(255, 196, 50)))
                using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }

            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }

    // Tall rounded brightness pill in the style of the iOS Control Center: white fill rises
    // from the bottom, a sun sits near the bottom and its rays grow with brightness.
    class Slider : Control
    {
        int value;
        bool dragging, moved;
        int startY, startValue;
        public event EventHandler ValueChanged;   // user changes only

        public Slider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public bool Dragging { get { return dragging; } }

        public int Value
        {
            get { return value; }
            set { int v = Math.Max(0, Math.Min(100, value)); if (v == this.value) return; this.value = v; Invalidate(); }
        }

        void SetFromUser(int v)
        {
            v = Math.Max(0, Math.Min(100, v));
            if (v == value) return;
            value = v;
            Invalidate();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        int ValueAt(int y) { return (int)Math.Round((Height - y) * 100.0 / Height); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            dragging = true; moved = false;
            startY = e.Y; startValue = value;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            if (!moved && Math.Abs(e.Y - startY) < 3) return;
            moved = true;
            // Like iOS: dragging moves the level by how far you drag, it doesn't jump to the pointer.
            SetFromUser(startValue + (int)Math.Round((startY - e.Y) * 100.0 / Height));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging && !moved) SetFromUser(ValueAt(e.Y));   // a plain click sets the level there
            dragging = false;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var he = e as HandledMouseEventArgs;
            if (he != null) he.Handled = true;
            int step = (ModifierKeys & Keys.Control) != 0 ? 1 : 5;
            SetFromUser(value + Math.Sign(e.Delta) * step);
        }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Down: SetFromUser(value - 1); break;
                case Keys.Up: SetFromUser(value + 1); break;
                case Keys.PageDown: SetFromUser(value - 10); break;
                case Keys.PageUp: SetFromUser(value + 10); break;
                case Keys.Home: SetFromUser(0); break;
                case Keys.End: SetFromUser(100); break;
            }
        }

        public static GraphicsPath Pill(float x, float y, float w, float h)
        {
            float d = Math.Min(w, h) * 0.9f;
            var p = new GraphicsPath();
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int W = Width, H = Height;
            if (W < 4 || H < 4) return;
            int fillTop = (int)Math.Round(H * (1 - value / 100f));

            // Paint the two-tone inside into a bitmap, then fill the pill with it so the edges stay smooth.
            using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            {
                using (var bg = Graphics.FromImage(bmp))
                {
                    using (var top = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(84, 84, 96), Color.FromArgb(68, 68, 80), 90f))
                        bg.FillRectangle(top, 0, 0, W, H);
                    using (var white = new SolidBrush(Color.FromArgb(248, 248, 252)))
                        bg.FillRectangle(white, 0, fillTop, W, H - fillTop);
                }
                using (var path = Pill(0.5f, 0.5f, W - 1.5f, H - 1.5f))
                {
                    using (var tb = new TextureBrush(bmp)) g.FillPath(tb, path);
                    using (var pen = new Pen(Color.FromArgb(dragging ? 150 : 110, 0, 0, 0), 1.2f)) g.DrawPath(pen, path);
                }
            }

            // Sun near the bottom; yellow on the white fill, pale grey once the fill drops below it.
            float cx = W / 2f, cy = H - W * 0.5f;
            float core = W * 0.095f, r1 = W * 0.155f, r2 = r1 + W * (0.025f + 0.05f * value / 100f);
            bool onWhite = fillTop <= cy - r2;
            Color sun = onWhite ? Color.FromArgb(246, 200, 62) : Color.FromArgb(190, 190, 200);
            Color ray = onWhite ? Color.FromArgb(247, 214, 110) : Color.FromArgb(170, 170, 182);
            using (var b = new SolidBrush(sun)) g.FillEllipse(b, cx - core, cy - core, core * 2, core * 2);
            using (var p = new Pen(ray, Math.Max(2f, W * 0.05f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4 + Math.PI / 8;
                    float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
                    g.DrawLine(p, cx + dx * r1, cy + dy * r1, cx + dx * r2, cy + dy * r2);
                }
            }
        }
    }

    // Warm screen (low blue light): BrightLock's own filter, separate from Windows'
    // Night light. It scales each screen's gamma ramp (like f.lux) to about 3500 K.
    // Windows refuses ramps far from neutral unless an admin setting is changed, and
    // this warmth stays inside the default limit. The screen's own ramp (calibration)
    // is kept and restored when the filter goes off.
    static class Warm
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string device, string output, IntPtr init);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] ramp);
        [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

        const double Green = 0.80, Blue = 0.62;
        static readonly Dictionary<string, ushort[]> bases = new Dictionary<string, ushort[]>(StringComparer.OrdinalIgnoreCase);

        static IntPtr Open(string device)
        {
            var h = CreateDC(device, null, null, IntPtr.Zero);
            return h != IntPtr.Zero ? h : CreateDC("DISPLAY", device, null, IntPtr.Zero);
        }

        static ushort[] Identity()
        {
            var r = new ushort[768];
            for (int i = 0; i < 256; i++) r[i] = r[256 + i] = r[512 + i] = (ushort)(i * 257);
            return r;
        }

        static ushort[] Warmed(ushort[] b)
        {
            var r = (ushort[])b.Clone();
            for (int i = 0; i < 256; i++)
            {
                r[256 + i] = (ushort)(b[256 + i] * Green);
                r[512 + i] = (ushort)(b[512 + i] * Blue);
            }
            return r;
        }

        static bool Same(ushort[] a, ushort[] b)
        {
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 300) return false;
            return true;
        }

        // Applies (on) or removes (off) the filter on every screen; true if at least
        // one screen took it. Cheap enough to call every few seconds: a screen whose
        // ramp is already right is left alone.
        public static bool Apply(bool on)
        {
            bool any = false;
            foreach (var screen in Screen.AllScreens)
            {
                var hdc = Open(screen.DeviceName);
                if (hdc == IntPtr.Zero) continue;
                try
                {
                    var now = new ushort[768];
                    bool read = GetDeviceGammaRamp(hdc, now);
                    ushort[] b;
                    if (!bases.TryGetValue(screen.DeviceName, out b))
                    {
                        // Already tinted (left over from a crash, or another warm tool): start from neutral.
                        b = read && now[767] > now[255] * 0.9 ? now : Identity();
                        bases[screen.DeviceName] = b;
                    }
                    var want = on ? Warmed(b) : b;
                    if (read && Same(now, want)) { any = true; continue; }
                    if (SetDeviceGammaRamp(hdc, want)) any = true;
                    else Log.Write("Warm screen: " + screen.DeviceName + " refused the colour change");
                }
                finally { DeleteDC(hdc); }
            }
            if (!on) bases.Clear();
            return any;
        }
    }

    class GlyphButton : Control
    {
        bool on, hover;
        public Color OnColor = Theme.Accent;
        readonly string onGlyph, offGlyph;
        public event EventHandler Toggled;

        public GlyphButton(string onGlyph, string offGlyph, float size)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            this.onGlyph = onGlyph; this.offGlyph = offGlyph;
            Font = new Font("Segoe MDL2 Assets", size);
        }

        public bool On { get { return on; } set { on = value; Invalidate(); } }
        public bool Momentary;   // plain button: click does not flip On

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Momentary) on = !on;
            Invalidate();
            if (Toggled != null) Toggled(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hover)
                using (var b = new SolidBrush(Color.FromArgb(28, 255, 255, 255)))
                    g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(g, on ? onGlyph : offGlyph, Font, ClientRectangle, on ? OnColor : Theme.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    class Row
    {
        public Mon Mon;
        public Label Value;
        public Slider Slider;
        public GlyphButton Lock;
        public Chip Hdr;
    }

    // Small rounded text toggle ("HDR"). The app sets On from the real display state.
    class Chip : Control
    {
        bool on, hover, busy;
        readonly string text;

        public Chip(string text, Font font)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            this.text = text;
            Font = font;
        }

        public bool On { get { return on; } set { if (on != value) { on = value; Invalidate(); } } }
        public bool Busy { get { return busy; } set { if (busy != value) { busy = value; Invalidate(); } } }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Width - 1.5f, h = Height - 1.5f;
            using (var path = new GraphicsPath())
            {
                path.AddArc(0.5f, 0.5f, h, h, 90, 180);
                path.AddArc(0.5f + w - h, 0.5f, h, h, 270, 180);
                path.CloseFigure();
                Color fill = on ? (hover ? Color.FromArgb(110, 208, 255) : Theme.Accent) : Color.FromArgb(hover ? 40 : 22, 255, 255, 255);
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (!on) using (var p = new Pen(Color.FromArgb(50, 255, 255, 255))) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, busy ? "..." : text, Font, ClientRectangle,
                on ? Color.FromArgb(16, 18, 26) : (hover ? Theme.Text : Theme.Sub),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // Placeholder pill for a screen Windows is not using; click turns it back on.
    class OffPill : Control
    {
        bool hover;

        public OffPill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Slider.Pill(0.5f, 0.5f, Width - 1.5f, Height - 1.5f))
            {
                using (var b = new SolidBrush(hover ? Color.FromArgb(52, 52, 62) : Color.FromArgb(38, 38, 46))) g.FillPath(b, path);
                using (var p = new Pen(Color.FromArgb(70, 255, 255, 255)) { DashStyle = DashStyle.Dash }) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, ((char)0xE7E8).ToString(), Font, ClientRectangle, hover ? Theme.Text : Theme.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    class Popup : Form
    {
        static readonly string LockGlyph = ((char)0xE72E).ToString(), UnlockGlyph = ((char)0xE785).ToString(), LinkGlyph = ((char)0xE71B).ToString(),
            SleepGlyph = ((char)0xE708).ToString(), PowerGlyph = ((char)0xE7E8).ToString();

        readonly App app;
        readonly float scale;
        readonly ToolTip tips = new ToolTip();
        readonly Font titleFont, nameFont, valueFont, smallFont, chipFont, glyphFont;
        readonly List<Row> rows = new List<Row>();
        GlyphButton linkButton, nightButton;
        Label footer;
        public DateTime HiddenAt, KeepOpenUntil;

        public Popup(App app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            KeyPreview = true;
            DoubleBuffered = true;
            Text = "BrightLock";
            titleFont = new Font("Segoe UI Semibold", 11f);
            nameFont = new Font("Segoe UI Semibold", 9.5f);
            valueFont = new Font("Segoe UI", 9f);
            smallFont = new Font("Segoe UI", 8.25f);
            chipFont = new Font("Segoe UI Semibold", 8.25f);
            glyphFont = new Font("Segoe MDL2 Assets", 9f);
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: no Alt+Tab entry
                cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1, round = 2, border = 0x00302B2B;
            Native.DwmSetWindowAttribute(Handle, 20, ref on, 4);      // dark mode
            Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);   // rounded corners (Windows 11)
            Native.DwmSetWindowAttribute(Handle, 34, ref border, 4);  // border colour
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new LinearGradientBrush(ClientRectangle, Color.FromArgb(30, 31, 42), Color.FromArgb(15, 16, 21), 90f))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        Label AddLabel(string text, Font font, Color color, Rectangle bounds, ContentAlignment align)
        {
            var l = new Label
            {
                Text = text, Font = font, ForeColor = color, Bounds = bounds, TextAlign = align,
                AutoEllipsis = true, BackColor = Color.Transparent, UseMnemonic = false
            };
            Controls.Add(l);
            return l;
        }

        public void Build(List<Mon> mons, bool scanning, bool linked, List<DisplayCfg.Inactive> off)
        {
            int oldBottom = Visible ? Bottom : -1;
            SuspendLayout();
            var old = Controls.Cast<Control>().ToList();
            Controls.Clear();
            foreach (var c in old) c.Dispose();
            rows.Clear();
            linkButton = null;
            nightButton = null;

            int pad = S(20), pillW = S(96), pillH = S(236), gap = S(22);
            int cols = mons.Count + off.Count;
            int n = Math.Max(1, cols);
            int w = Math.Max(S(280), n * pillW + (n - 1) * gap + 2 * pad);
            int inner = w - 2 * pad;
            int y = S(14);

            AddLabel("Brightness", titleFont, Theme.Text, new Rectangle(pad, y, inner - S(76), S(28)), ContentAlignment.MiddleLeft);
            int bx = w - pad - S(30);
            if (mons.Count > 1)
            {
                linkButton = new GlyphButton(LinkGlyph, LinkGlyph, 11f) { Bounds = new Rectangle(bx, y - S(1), S(30), S(30)), On = linked };
                tips.SetToolTip(linkButton, "Move all monitors together");
                var lb = linkButton;
                linkButton.Toggled += (s, e) => app.SetLinked(lb.On);
                Controls.Add(linkButton);
                bx -= S(34);
            }
            {
                var nl = new GlyphButton(SleepGlyph, SleepGlyph, 11f)
                {
                    Bounds = new Rectangle(bx, y - S(1), S(30), S(30)), On = app.WarmOn, OnColor = Color.FromArgb(255, 170, 70)
                };
                tips.SetToolTip(nl, "Warm screen (low blue light)\nBrightLock tints all screens warmer. Separate from Windows' Night light.");
                nl.Toggled += (s, e) => { if (!app.SetWarm(nl.On)) nl.On = !nl.On; };
                Controls.Add(nl);
                nightButton = nl;
            }
            y += S(28) + S(14);

            if (cols == 0)
            {
                AddLabel(scanning ? "Looking for monitors..." : "No monitor answered over DDC/CI.\nTurn on DDC/CI in the monitor's menu.",
                    valueFont, Theme.Sub, new Rectangle(pad, y, inner, S(48)), ContentAlignment.MiddleLeft);
                y += S(56);
            }
            else
            {
                int x = pad + (inner - (cols * pillW + (cols - 1) * gap)) / 2;
                int colW = pillW + gap;
                int ch = S(24), cg = S(6);
                bool canOff = mons.Count > 1;
                bool anyChips = canOff || off.Count > 0 || mons.Any(mm => { var ht = app.HdrFor(mm); return ht != null && ht.Supported; });

                foreach (var m in mons)
                {
                    var row = new Row { Mon = m };
                    var mon = m;
                    row.Slider = new Slider { Bounds = new Rectangle(x, y, pillW, pillH) };
                    var sl = row.Slider;
                    row.Slider.ValueChanged += (s, e) => app.SetPct(mon, sl.Value);
                    Controls.Add(row.Slider);

                    int ly = y + pillH + S(10);
                    AddLabel(m.Name, nameFont, Theme.Text, new Rectangle(x - gap / 2, ly, colW, S(20)), ContentAlignment.MiddleCenter);
                    ly += S(21);
                    int lockW = S(26);
                    row.Value = AddLabel("", valueFont, Theme.Sub, new Rectangle(x, ly, pillW / 2 + S(6), S(26)), ContentAlignment.MiddleRight);
                    row.Lock = new GlyphButton(LockGlyph, UnlockGlyph, 10f) { Bounds = new Rectangle(x + pillW / 2 + S(10), ly, lockW, lockW), On = m.Locked };
                    tips.SetToolTip(row.Lock, "Locked: if the monitor or another app changes the brightness, BrightLock puts it back.\nClick to unlock.");
                    var lk = row.Lock;
                    row.Lock.Toggled += (s, e) => app.SetLocked(mon, lk.On);
                    Controls.Add(row.Lock);

                    // Chip row: HDR and screen off.
                    var hdr = app.HdrFor(m);
                    bool hdrOk = hdr != null && hdr.Supported;
                    int cw = S(54), pw = S(34);
                    int total = (hdrOk ? cw : 0) + (canOff ? pw : 0) + (hdrOk && canOff ? cg : 0);
                    int cx = x + (pillW - total) / 2, cy = ly + S(26) + S(6);
                    if (hdrOk)
                    {
                        row.Hdr = new Chip("HDR", chipFont) { Bounds = new Rectangle(cx, cy, cw, ch) };
                        tips.SetToolTip(row.Hdr, "Turn HDR on or off for this monitor.\nMany monitors ignore brightness changes while HDR is on.");
                        row.Hdr.Click += (s, e) => app.ToggleHdr(mon);
                        Controls.Add(row.Hdr);
                        cx += cw + cg;
                    }
                    if (canOff)
                    {
                        var pc = new Chip(PowerGlyph, glyphFont) { Bounds = new Rectangle(cx, cy, pw, ch) };
                        tips.SetToolTip(pc, "Turn this screen off.\nWindows stops using it (like Win+P \"show only on\" the other screen) and it goes to sleep.\nTurn it back on from here.");
                        pc.Click += (s, e) => app.TurnOff(mon);
                        Controls.Add(pc);
                    }

                    rows.Add(row);
                    x += colW;
                }

                foreach (var o in off)
                {
                    var pill = new OffPill { Bounds = new Rectangle(x, y, pillW, pillH), Font = new Font("Segoe MDL2 Assets", 20f) };
                    tips.SetToolTip(pill, "This screen is off. Click to turn it back on.");
                    pill.Click += (s, e) => app.TurnOn();
                    Controls.Add(pill);

                    int ly = y + pillH + S(10);
                    AddLabel(o.Name, nameFont, Theme.Sub, new Rectangle(x - gap / 2, ly, colW, S(20)), ContentAlignment.MiddleCenter);
                    ly += S(21);
                    AddLabel("Off", valueFont, Theme.Sub, new Rectangle(x, ly, pillW, S(26)), ContentAlignment.MiddleCenter);
                    int ow = S(76);
                    var on = new Chip("Turn on", chipFont) { Bounds = new Rectangle(x + (pillW - ow) / 2, ly + S(26) + S(6), ow, ch), On = true };
                    on.Click += (s, e) => app.TurnOn();
                    Controls.Add(on);
                    x += colW;
                }

                y += pillH + S(10) + S(21) + S(26) + S(12) + (anyChips ? S(30) : 0);
            }

            footer = AddLabel("", smallFont, Theme.Sub, new Rectangle(pad, y, inner, S(46)), ContentAlignment.TopCenter);
            footer.AutoEllipsis = false;
            y += S(46) + S(6);

            ClientSize = new Size(w, y);
            if (oldBottom >= 0) Top = oldBottom - Height;   // grow upwards from the taskbar
            ResumeLayout();
            RefreshValues();
        }

        public void RefreshValues()
        {
            if (nightButton != null) nightButton.On = app.WarmOn;
            foreach (var r in rows)
            {
                int t = r.Mon.TargetPct;
                r.Value.Text = t < 0 ? "-" : t + "%";
                if (t >= 0 && !r.Slider.Dragging) r.Slider.Value = t;
                r.Lock.On = r.Mon.Locked;
                if (r.Hdr != null)
                {
                    var h = app.HdrFor(r.Mon);
                    r.Hdr.On = h != null && h.On;
                    r.Hdr.Busy = app.HdrBusy(r.Mon);
                }
            }
        }

        public void SetFooter(string text) { if (footer != null) footer.Text = text; }

        public void ShowNear(Point p)
        {
            var screen = Screen.FromPoint(p);
            Rectangle wa = screen.WorkingArea, sb = screen.Bounds;
            int m = S(12), x, y;
            if (wa.Bottom < sb.Bottom && p.Y >= wa.Bottom) { x = p.X - Width / 2; y = wa.Bottom - Height - m; }
            else if (wa.Top > sb.Top && p.Y < wa.Top) { x = p.X - Width / 2; y = wa.Top + m; }
            else if (wa.Left > sb.Left && p.X < wa.Left) { x = wa.Left + m; y = p.Y - Height / 2; }
            else if (wa.Right < sb.Right && p.X >= wa.Right) { x = wa.Right - Width - m; y = p.Y - Height / 2; }
            else { x = wa.Right - Width - m; y = wa.Bottom - Height - m; }
            x = Math.Max(wa.Left + m, Math.Min(x, wa.Right - Width - m));
            y = Math.Max(wa.Top + m, Math.Min(y, wa.Bottom - Height - m));
            Location = new Point(x, y);
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
            if (rows.Count > 0) rows[0].Slider.Focus();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (DateTime.UtcNow < KeepOpenUntil) return;   // switching HDR briefly steals focus
            Hide();
            HiddenAt = DateTime.UtcNow;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            base.OnFormClosing(e);
        }
    }

    // Windows display-config API: HDR on/off (same switch as Settings > Display > HDR) and turning
    // a screen off or back on the way Win+P does ("show only on 1", "extend").
    static class DisplayCfg
    {
        [StructLayout(LayoutKind.Sequential)] internal struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct HEADER { public int type; public uint size; public LUID adapterId; public uint id; }
        [StructLayout(LayoutKind.Sequential)] struct PATH_SOURCE { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
        [StructLayout(LayoutKind.Sequential)] struct RATIONAL { public uint n, d; }
        [StructLayout(LayoutKind.Sequential)]
        struct PATH_TARGET
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public int outputTechnology; public int rotation;
            public int scaling; public RATIONAL refresh; public int scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }
        [StructLayout(LayoutKind.Sequential)] struct PATH { public PATH_SOURCE src; public PATH_TARGET tgt; public uint flags; }
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        struct MODE
        {
            [FieldOffset(0)] public int infoType;   // 1 = source mode
            [FieldOffset(4)] public uint id;
            [FieldOffset(8)] public LUID adapterId;
            [FieldOffset(28)] public int posX;      // source mode desktop position
            [FieldOffset(32)] public int posY;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TARGET_NAME
        {
            public HEADER h; public uint flags; public int outputTechnology; public ushort mfg; public ushort product; public uint connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
        }
        [StructLayout(LayoutKind.Sequential)] struct COLOR_INFO { public HEADER h; public uint value; public int encoding; public uint bits; }
        [StructLayout(LayoutKind.Sequential)] struct COLOR_INFO2 { public HEADER h; public uint value; public int encoding; public uint bits; public int activeColorMode; }
        [StructLayout(LayoutKind.Sequential)] struct SET_STATE { public HEADER h; public uint value; }

        const uint QDC_ALL_PATHS = 1, QDC_ONLY_ACTIVE_PATHS = 2;
        const uint SDC_TOPOLOGY_EXTEND = 0x4, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_APPLY = 0x80,
                   SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400;
        const uint PATH_ACTIVE = 1, MODE_IDX_INVALID = 0xFFFFFFFF;

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH[] paths, ref uint numModes, [Out] MODE[] modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, [In] PATH[] paths, uint numModes, [In] MODE[] modes, uint flags);
        [DllImport("user32.dll", EntryPoint = "SetDisplayConfig")] static extern int SetDisplayConfigTopology(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO2 info);
        [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SET_STATE info);

        public class Target
        {
            internal LUID Adapter; internal uint Id; internal bool NewApi;
            public bool Supported, On;
        }

        // A connected screen that Windows currently isn't using.
        public class Inactive { public string Key, Name; }

        static HEADER Header(int type, Type t, LUID adapter, uint id)
        {
            return new HEADER { type = type, size = (uint)Marshal.SizeOf(t), adapterId = adapter, id = id };
        }

        static bool Query(uint flags, out PATH[] paths, out MODE[] modes)
        {
            paths = null; modes = null;
            uint np, nm;
            if (GetDisplayConfigBufferSizes(flags, out np, out nm) != 0) return false;
            paths = new PATH[np]; modes = new MODE[nm];
            if (QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
            Array.Resize(ref paths, (int)np); Array.Resize(ref modes, (int)nm);
            return true;
        }

        static bool TargetName(PATH_TARGET t, out string key, out string name)
        {
            key = null; name = null;
            var n = new TARGET_NAME { h = Header(2, typeof(TARGET_NAME), t.adapterId, t.id) };
            if (DisplayConfigGetDeviceInfo(ref n) != 0) return false;
            key = Displays.InstanceFromDeviceId(n.devicePath);
            name = n.friendlyName;
            return key != null;
        }

        // Keyed like Mon.Key: "DISPLAY\ABC1234\5&1a&0&UID1234".
        public static Dictionary<string, Target> QueryHdr()
        {
            var result = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
            try
            {
                PATH[] paths; MODE[] modes;
                if (!Query(QDC_ONLY_ACTIVE_PATHS, out paths, out modes)) return result;
                foreach (var p in paths)
                {
                    string key, name;
                    if (!TargetName(p.tgt, out key, out name)) continue;
                    LUID a = p.tgt.adapterId; uint id = p.tgt.id;
                    var t = new Target { Adapter = a, Id = id };
                    var c2 = new COLOR_INFO2 { h = Header(15, typeof(COLOR_INFO2), a, id) };
                    if (DisplayConfigGetDeviceInfo(ref c2) == 0)
                    {
                        t.NewApi = true;
                        t.Supported = (c2.value & 0x10) != 0;   // highDynamicRangeSupported
                        t.On = c2.activeColorMode == 2;         // DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR
                    }
                    else
                    {
                        var c = new COLOR_INFO { h = Header(9, typeof(COLOR_INFO), a, id) };
                        if (DisplayConfigGetDeviceInfo(ref c) != 0) continue;
                        t.Supported = (c.value & 1) != 0;
                        t.On = (c.value & 2) != 0;
                    }
                    result[key] = t;
                }
            }
            catch { }
            return result;
        }

        public static bool SetHdr(Target t, bool on)
        {
            var s = new SET_STATE { h = Header(t.NewApi ? 16 : 10, typeof(SET_STATE), t.Adapter, t.Id), value = on ? 1u : 0u };
            return DisplayConfigSetDeviceInfo(ref s) == 0;
        }

        public static List<Inactive> QueryInactive()
        {
            var result = new List<Inactive>();
            try
            {
                PATH[] paths; MODE[] modes;
                if (!Query(QDC_ALL_PATHS, out paths, out modes)) return result;
                var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in paths)
                {
                    if ((p.flags & PATH_ACTIVE) == 0) continue;
                    string key, name;
                    if (TargetName(p.tgt, out key, out name)) active.Add(key);
                }
                foreach (var p in paths)
                {
                    if ((p.flags & PATH_ACTIVE) != 0 || p.tgt.targetAvailable == 0) continue;
                    string key, name;
                    if (!TargetName(p.tgt, out key, out name) || active.Contains(key) || !seen.Add(key)) continue;
                    result.Add(new Inactive { Key = key, Name = string.IsNullOrEmpty(name) ? "Display" : name });
                }
            }
            catch { }
            return result;
        }

        // Stops Windows using one screen; the others stay as they are.
        public static bool TurnOff(string key)
        {
            PATH[] paths; MODE[] modes;
            if (!Query(QDC_ONLY_ACTIVE_PATHS, out paths, out modes) || paths.Length < 2) return false;
            int idx = -1;
            for (int i = 0; i < paths.Length; i++)
            {
                string k, n;
                if (TargetName(paths[i].tgt, out k, out n) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            }
            if (idx < 0) return false;

            // If this screen was the main one, move the desktop so another screen sits at (0,0).
            uint srcMode = paths[idx].src.modeInfoIdx;
            bool wasPrimary = srcMode < modes.Length && modes[srcMode].posX == 0 && modes[srcMode].posY == 0;
            if (wasPrimary)
            {
                int other = idx == 0 ? 1 : 0;
                uint om = paths[other].src.modeInfoIdx;
                if (om < modes.Length)
                {
                    int dx = modes[om].posX, dy = modes[om].posY;
                    for (int i = 0; i < modes.Length; i++)
                        if (modes[i].infoType == 1 && i != srcMode) { modes[i].posX -= dx; modes[i].posY -= dy; }
                }
            }

            paths[idx].flags &= ~PATH_ACTIVE;
            paths[idx].src.modeInfoIdx = MODE_IDX_INVALID;
            paths[idx].tgt.modeInfoIdx = MODE_IDX_INVALID;
            return SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE) == 0;
        }

        // Turns every connected screen back on using the last "Extend" layout Windows remembers.
        public static bool ExtendAll()
        {
            return SetDisplayConfigTopology(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND) == 0;
        }
    }

    class App : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly Store store;
        readonly Popup popup;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem startupItem;
        readonly System.Windows.Forms.Timer saveTimer, rescanTimer, healthTimer, warmTimer;
        readonly EventWaitHandle showSignal;
        List<Mon> mons = new List<Mon>();
        bool scanning, rescanAgain, exiting;
        Dictionary<string, DisplayCfg.Target> hdr = new Dictionary<string, DisplayCfg.Target>();
        readonly HashSet<string> hdrBusy = new HashSet<string>();
        List<DisplayCfg.Inactive> offList = new List<DisplayCfg.Inactive>();
        int putBackCount;
        string lastPutBack;

        public App()
        {
            store = Store.Load();
            popup = new Popup(this);
            var forceHandle = popup.Handle;

            var menu = new ContextMenuStrip();
            startupItem = new ToolStripMenuItem("Start with Windows", null, (s, e) => { SetStartup(!IsStartup()); startupItem.Checked = IsStartup(); });
            menu.Items.Add(startupItem);
            menu.Items.Add("Lock all monitors", null, (s, e) => { foreach (var m in mons) SetLocked(m, true); popup.RefreshValues(); });
            menu.Items.Add("Unlock all monitors", null, (s, e) => { foreach (var m in mons) SetLocked(m, false); popup.RefreshValues(); });
            menu.Items.Add("Find monitors again", null, (s, e) => DoRescan());
            menu.Items.Add("Put all screens to sleep", null, (s, e) => SleepScreens());
            menu.Items.Add("Turn all screens back on", null, (s, e) => TurnOn());
            menu.Items.Add("Open log", null, (s, e) => { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); } catch { } });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => Exit());

            tray = new NotifyIcon { Icon = TrayIconForTheme(), Text = "BrightLock", Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };

            saveTimer = new System.Windows.Forms.Timer { Interval = 800 };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); store.Save(); };
            rescanTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            rescanTimer.Tick += (s, e) => DoRescan();
            healthTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            healthTimer.Tick += (s, e) => { if (mons.Any(m => m.Failures >= 5)) RequestRescan(); };
            healthTimer.Start();
            // Windows drops gamma ramps on display changes, sleep and some full-screen
            // apps; while the warm filter is on it is put back every few seconds.
            warmTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            warmTimer.Tick += (s, e) => { if (store.Warm) Warm.Apply(true); };
            warmTimer.Start();
            if (store.Warm) Warm.Apply(true);

            SystemEvents.DisplaySettingsChanged += OnDisplayChange;
            SystemEvents.PowerModeChanged += OnPowerChange;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.UserPreferenceChanged += OnPrefChange;

            if (!store.FirstRunDone) { SetStartup(true); store.FirstRunDone = true; store.Save(); }
            else if (IsStartup()) SetStartup(true);   // keep the path current if the exe moved
            startupItem.Checked = IsStartup();

            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "BrightLock.Show");
            ThreadPool.RegisterWaitForSingleObject(showSignal, (o, t) => OnUi(ShowPopup), null, -1, false);

            Log.Write("Started");
            popup.Build(mons, true, store.Linked, offList);
            DoRescan();
        }

        void OnUi(Action a)
        {
            if (exiting) return;
            try { popup.BeginInvoke(a); } catch { }
        }

        void OnDisplayChange(object s, EventArgs e) { OnUi(RequestRescan); }
        void OnPowerChange(object s, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) OnUi(RequestRescan); }
        void OnSessionSwitch(object s, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect) OnUi(RequestRescan);
        }
        void OnPrefChange(object s, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General) OnUi(() => tray.Icon = TrayIconForTheme());
        }

        static Icon TrayIconForTheme()
        {
            return IconArt.Tray(Theme.TaskbarIsLight() ? Color.FromArgb(30, 30, 30) : Color.White);
        }

        void RequestRescan() { rescanTimer.Stop(); rescanTimer.Start(); }

        void DoRescan()
        {
            rescanTimer.Stop();
            if (scanning) { rescanAgain = true; return; }
            scanning = true;
            var old = mons;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var m in old) m.Stop();
                List<Mon> found;
                try { found = Displays.Enumerate(); } catch { found = new List<Mon>(); }
                OnUi(() => Adopt(found));
            });
        }

        void Adopt(List<Mon> found)
        {
            mons = found;
            foreach (var m in mons)
            {
                Entry e;
                if (store.Monitors.TryGetValue(m.Key, out e)) { m.TargetPct = e.Pct; m.Locked = e.Locked; }
                m.PollMs = store.PollSeconds * 1000;
                m.Adopted += mon => OnUi(() => { Remember(mon); popup.RefreshValues(); UpdateTooltip(); });
                m.PutBack += (mon, from, to) => OnUi(() => OnPutBack(mon, from, to));
                m.Start();
            }
            Log.Write("Monitors: " + (mons.Count == 0 ? "none" : string.Join(", ", mons.Select(m => m.Name))));
            hdr = DisplayCfg.QueryHdr();
            offList = DisplayCfg.QueryInactive();
            popup.Build(mons, false, store.Linked, offList);
            if (popup.Visible && !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(popup.Bounds))) popup.Hide();
            UpdateFooter();
            UpdateTooltip();
            scanning = false;
            if (rescanAgain) { rescanAgain = false; RequestRescan(); }
        }

        void OnPutBack(Mon m, int from, int to)
        {
            putBackCount++;
            lastPutBack = DateTime.Now.ToString("HH:mm") + ", " + m.Name + " " + from + "% -> " + to + "%";
            Log.Write(m.Name + ": brightness was changed to " + from + "% by something else; set back to " + to + "%");
            UpdateFooter();
        }

        void UpdateFooter()
        {
            string text;
            if (mons.Count == 0) text = "";
            else if (!mons.Any(m => m.Locked)) text = "Nothing is locked. Click the lock to hold a monitor's brightness.";
            else if (putBackCount == 0) text = "Locked monitors are checked every " + store.PollSeconds + " s and set back if their brightness changes.";
            else text = "Set back " + putBackCount + "x since start. Last: " + lastPutBack;
            popup.SetFooter(text);
        }

        void UpdateTooltip()
        {
            var sb = new StringBuilder("BrightLock");
            foreach (var m in mons)
                sb.Append('\n').Append(m.Name).Append(' ').Append(m.TargetPct < 0 ? "-" : m.TargetPct + "%");
            string t = sb.ToString();
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void Remember(Mon m)
        {
            if (m.TargetPct < 0) return;
            store.Monitors[m.Key] = new Entry { Pct = m.TargetPct, Locked = m.Locked };
            saveTimer.Stop(); saveTimer.Start();
        }

        public void SetLinked(bool linked)
        {
            store.Linked = linked;
            saveTimer.Stop(); saveTimer.Start();
        }

        public void SetPct(Mon m, int pct)
        {
            if (store.Linked && mons.Count > 1) { SetAll(pct); return; }
            m.SetPct(pct);
            Remember(m);
            popup.RefreshValues();
            UpdateTooltip();
        }

        public void SetAll(int pct)
        {
            foreach (var m in mons) { m.SetPct(pct); Remember(m); }
            popup.RefreshValues();
            UpdateTooltip();
        }

        public void SetLocked(Mon m, bool locked)
        {
            m.Locked = locked;
            Remember(m);
            if (locked) m.PollNow();
            UpdateFooter();
        }

        void TogglePopup()
        {
            if (popup.Visible) { popup.Hide(); return; }
            // A click on the tray icon first closes the open popup (it loses focus); don't reopen it.
            if ((DateTime.UtcNow - popup.HiddenAt).TotalMilliseconds < 300) return;
            ShowPopup();
        }

        public DisplayCfg.Target HdrFor(Mon m)
        {
            DisplayCfg.Target t;
            return hdr.TryGetValue(m.Key, out t) ? t : null;
        }

        public bool HdrBusy(Mon m) { return hdrBusy.Contains(m.Key); }

        public void ToggleHdr(Mon m)
        {
            var t = HdrFor(m);
            if (t == null || hdrBusy.Contains(m.Key)) return;
            bool want = !t.On;
            string key = m.Key, name = m.Name;
            hdrBusy.Add(key);
            popup.KeepOpenUntil = DateTime.UtcNow.AddSeconds(4);
            popup.RefreshValues();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = DisplayCfg.SetHdr(t, want);
                OnUi(() =>
                {
                    hdrBusy.Remove(key);
                    hdr = DisplayCfg.QueryHdr();
                    Log.Write(name + ": HDR " + (want ? "on" : "off") + (ok ? "" : " (Windows refused)"));
                    popup.RefreshValues();
                    popup.KeepOpenUntil = DateTime.UtcNow.AddSeconds(1);
                    popup.Activate();
                });
            });
        }

        public void TurnOff(Mon m)
        {
            if (mons.Count < 2) return;   // never switch off the last screen
            string key = m.Key, name = m.Name;
            popup.KeepOpenUntil = DateTime.UtcNow.AddSeconds(6);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = DisplayCfg.TurnOff(key);
                OnUi(() =>
                {
                    Log.Write(name + (ok ? ": turned off" : ": Windows refused to turn it off"));
                    if (!ok) popup.SetFooter("Windows refused to turn off " + name + ".");
                    RequestRescan();
                });
            });
        }

        public void TurnOn()
        {
            popup.KeepOpenUntil = DateTime.UtcNow.AddSeconds(6);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = DisplayCfg.ExtendAll();
                OnUi(() =>
                {
                    Log.Write(ok ? "All screens turned back on" : "Windows refused to turn the screens back on");
                    if (!ok) popup.SetFooter("Windows refused to turn the screen back on. Try Win+P > Extend.");
                    RequestRescan();
                });
            });
        }

        public void SleepScreens()
        {
            popup.Hide();
            // Wait a moment so the click itself does not wake the screens straight away.
            var t = new System.Windows.Forms.Timer { Interval = 700 };
            t.Tick += (s, e) =>
            {
                t.Stop(); t.Dispose();
                Native.SendMessage(popup.Handle, 0x0112, (IntPtr)0xF170, (IntPtr)2);   // WM_SYSCOMMAND, SC_MONITORPOWER, off
            };
            t.Start();
        }

        void ShowPopup()
        {
            hdr = DisplayCfg.QueryHdr();
            foreach (var m in mons) m.PollNow();
            popup.RefreshValues();
            UpdateFooter();
            popup.ShowNear(Cursor.Position);
        }

        static bool IsStartup()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("BrightLock") != null; }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("BrightLock", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("BrightLock", false);
                }
            }
            catch { }
        }

        public bool WarmOn { get { return store.Warm; } }

        public bool SetWarm(bool on)
        {
            if (!Warm.Apply(on) && on) { Warm.Apply(false); return false; }
            store.Warm = on;
            store.Save();
            Log.Write(on ? "Warm screen on" : "Warm screen off");
            return true;
        }

        void Exit()
        {
            exiting = true;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChange;
            SystemEvents.PowerModeChanged -= OnPowerChange;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.UserPreferenceChanged -= OnPrefChange;
            tray.Visible = false;
            warmTimer.Stop();
            if (store.Warm) Warm.Apply(false);
            store.Save();
            foreach (var m in mons) m.Stop();
            Log.Write("Exited");
            ExitThread();
        }
    }
}
