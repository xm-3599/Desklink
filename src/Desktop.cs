using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Desklink
{
    public interface IDesktop
    {
        Rectangle Bounds { get; }
        Point CursorPosition { get; }
        byte[] Frame(string quality);
        void PointAt(double x, double y);
        void Button(bool down, bool right);
        void Wheel(int delta);
        void Text(string value);
        void Keys(ushort[] keys);
    }

    public sealed class WindowsDesktop : IDesktop
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] events, int size);
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint frame, IntPtr brush, uint flags);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr prev);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr target, int x, int y, int w, int h,
            IntPtr source, int sx, int sy, int sw, int sh, uint raster);

        [StructLayout(LayoutKind.Sequential)] struct Input { public uint type; public InputBody body; }
        [StructLayout(LayoutKind.Explicit)] struct InputBody
        {
            [FieldOffset(0)] public Mouse mouse;
            [FieldOffset(0)] public Key key;
        }
        [StructLayout(LayoutKind.Sequential)] struct Mouse { public int x, y; public uint data, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct Key { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct CursorInfo { public int size, flags; public IntPtr handle; public Point position; }
        [StructLayout(LayoutKind.Sequential)] struct IconInfo { public int icon; public int x, y; public IntPtr mask, color; }
        static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");

        public static void SetDpiMode()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }
            catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
        }
        public Rectangle Bounds { get { return Screen.PrimaryScreen.Bounds; } }
        public Point CursorPosition { get { return Cursor.Position; } }
        public byte[] Frame(string quality)
        {
            Rectangle b = Bounds;
            int limit = quality == "sharp" ? 1920 : quality == "light" ? 960 : 1440;
            long level = quality == "sharp" ? 84 : quality == "light" ? 52 : 70;
            double scale = Math.Min(1, (double)limit / Math.Max(b.Width, b.Height));
            int w = Math.Max(1, (int)Math.Round(b.Width * scale));
            int h = Math.Max(1, (int)Math.Round(b.Height * scale));
            using (var frame = new Bitmap(w, h, PixelFormat.Format32bppRgb))
            using (var g = Graphics.FromImage(frame))
            using (var options = new EncoderParameters(1))
            using (var stream = new MemoryStream())
            {
                IntPtr source = GetDC(IntPtr.Zero);
                if (source == IntPtr.Zero) throw new InvalidOperationException("无法获取当前桌面");
                try
                {
                    var dest = g.GetHdc();
                    try
                    {
                        SetStretchBltMode(dest,4);
                        SetBrushOrgEx(dest,0,0,IntPtr.Zero);
                        if (!StretchBlt(dest,0,0,w,h,source,b.Left,b.Top,b.Width,b.Height,0x40CC0020))
                            throw new InvalidOperationException("无法读取当前桌面");
                        DrawCursor(dest,b,scale);
                    }
                    finally { g.ReleaseHdc(dest); }
                }
                finally { ReleaseDC(IntPtr.Zero,source); }
                options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, level);
                frame.Save(stream,Jpeg,options);
                return stream.ToArray();
            }
        }
        static void DrawCursor(IntPtr dc, Rectangle b, double scale)
        {
            var c = new CursorInfo { size = Marshal.SizeOf(typeof(CursorInfo)) };
            IconInfo icon;
            if (!GetCursorInfo(ref c) || (c.flags & 1) == 0 || !GetIconInfo(c.handle, out icon)) return;
            try
            {
                Size size = SystemInformation.CursorSize;
                DrawIconEx(dc,(int)((c.position.X-b.X-icon.x)*scale),(int)((c.position.Y-b.Y-icon.y)*scale),c.handle,
                    Math.Max(1,(int)(size.Width*scale)),Math.Max(1,(int)(size.Height*scale)),0,IntPtr.Zero,3);
            }
            finally { if (icon.mask != IntPtr.Zero) DeleteObject(icon.mask); if (icon.color != IntPtr.Zero) DeleteObject(icon.color); }
        }
        public void PointAt(double x, double y)
        {
            Rectangle b = Bounds;
            if (!SetCursorPos(b.Left + (int)Math.Round(x * (b.Width - 1)), b.Top + (int)Math.Round(y * (b.Height - 1))))
                throw new InvalidOperationException("当前桌面不接受输入");
        }
        static void Send(params Input[] events)
        {
            if (SendInput((uint)events.Length, events, Marshal.SizeOf(typeof(Input))) != events.Length)
                throw new InvalidOperationException("Windows 未接收输入，请检查电脑是否已解锁");
        }
        public void Button(bool down, bool right)
        {
            var e = new Input();
            e.body.mouse.flags = right ? (down ? 8u : 16u) : (down ? 2u : 4u);
            Send(e);
        }
        public void Wheel(int delta)
        {
            var e = new Input();
            e.body.mouse.flags = 0x0800;
            e.body.mouse.data = unchecked((uint)delta);
            Send(e);
        }
        static Input Keyboard(ushort key, ushort scan, uint flags)
        {
            var e = new Input { type = 1 };
            e.body.key = new Key { vk = key, scan = scan, flags = flags };
            return e;
        }
        public void Text(string value)
        {
            var events = new System.Collections.Generic.List<Input>();
            foreach (char c in value.Replace("\r\n","\n").Replace('\r','\n'))
            {
                if (c == '\n' || c == '\t')
                {
                    ushort k = c == '\n' ? (ushort)13 : (ushort)9;
                    events.Add(Keyboard(k, 0, 0)); events.Add(Keyboard(k, 0, 2));
                }
                else { events.Add(Keyboard(0, c, 4)); events.Add(Keyboard(0, c, 6)); }
            }
            if (events.Count > 0) Send(events.ToArray());
        }
        public void Keys(ushort[] keys)
        {
            var events = new System.Collections.Generic.List<Input>();
            foreach (ushort k in keys) events.Add(Keyboard(k, 0, 0));
            foreach (ushort k in keys.Reverse()) events.Add(Keyboard(k, 0, 2));
            Send(events.ToArray());
        }
    }
}
