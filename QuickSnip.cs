// QuickSnip - a Print Screen snipping tool that can run as admin, so it also works
// when an admin window is focused. Lives in the tray; Print Screen opens the snip overlay.
//
// Build: build.bat (uses the C# compiler that ships with Windows / .NET Framework 4.x)
// Written for C# 5 (the built-in compiler), so no newer syntax.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("QuickSnip")]
[assembly: System.Reflection.AssemblyProduct("QuickSnip")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace QuickSnip
{
    enum SnipMode { Rectangle = 0, Window = 1, Fullscreen = 2, Freeform = 3, Text = 4, ColorPicker = 5 }

    // ------------------------------------------------------------------ text recognition (Windows' built-in OCR)
    static class Ocr
    {
        public static Task<T> Wait<T>(global::Windows.Foundation.IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>();
            op.Completed = delegate(global::Windows.Foundation.IAsyncOperation<T> o, global::Windows.Foundation.AsyncStatus st)
            {
                if (st == global::Windows.Foundation.AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                else if (st == global::Windows.Foundation.AsyncStatus.Error) tcs.TrySetException(o.ErrorCode);
                else tcs.TrySetCanceled();
            };
            return tcs.Task;
        }

        // Reads all text in the image; lines are separated by new lines. Returns "" if there is none.
        public static async Task<string> Recognize(Bitmap src)
        {
            var lines = await RecognizeLines(src);
            var text = new List<string>();
            foreach (var l in lines) text.Add(l.Text);
            return string.Join("\r\n", text);
        }

        // Lines and words with their positions (in the source image's pixels).
        public static async Task<List<OcrLine>> RecognizeLines(Bitmap src)
        {
            var engine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) throw new InvalidOperationException("Windows text recognition isn't installed for your language.");

            float k = (src.Width < 1600 && src.Height < 1600) ? 2f : 1f;   // small UI text reads much better enlarged
            int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
            int max = (int)global::Windows.Media.Ocr.OcrEngine.MaxImageDimension;
            if (w > max || h > max) { float f = Math.Min((float)max / w, (float)max / h); w = (int)(w * f); h = (int)(h * f); }

            byte[] bytes;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, w, h));
                }
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                bytes = new byte[data.Stride * h];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                bmp.UnlockBits(data);
            }
            var writer = new global::Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(bytes);
            var buffer = writer.DetachBuffer();
            using (var sb = global::Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(buffer,
                       global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, w, h, global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied))
            {
                var result = await Wait(engine.RecognizeAsync(sb));
                float back = (float)src.Width / w;   // undo the enlargement
                var lines = new List<OcrLine>();
                foreach (var line in result.Lines)
                {
                    var ol = new OcrLine();
                    ol.Text = line.Text;
                    foreach (var word in line.Words)
                    {
                        var br = word.BoundingRect;
                        var ow = new OcrWord();
                        ow.Text = word.Text;
                        ow.Rect = new RectangleF((float)br.X * back, (float)br.Y * back, (float)br.Width * back, (float)br.Height * back);
                        ol.Words.Add(ow);
                    }
                    lines.Add(ol);
                }
                return lines;
            }
        }
    }

    class OcrWord { public string Text; public RectangleF Rect; }
    class OcrLine { public string Text; public readonly List<OcrWord> Words = new List<OcrWord>(); }

    // Small "Text copied" / "No text found" tag next to the mouse. Click-through, never takes focus, gone in ~1.5 s.
    class TextHint : Form
    {
        readonly string text;
        readonly float s;
        readonly System.Windows.Forms.Timer timer;

        public static void Popup(string text, Point near, float scale) { new TextHint(text, near, scale).Show(); }

        TextHint(string text, Point near, float scale)
        {
            this.text = text; s = scale;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Theme.Panel;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            using (var f = HintFont())
            {
                var sz = TextRenderer.MeasureText(text, f);
                Size = new Size(sz.Width + (int)(20 * s), sz.Height + (int)(12 * s));
            }
            var wa = System.Windows.Forms.Screen.FromPoint(near).WorkingArea;
            Location = new Point(Math.Min(near.X + (int)(16 * s), wa.Right - Width), Math.Min(near.Y + (int)(20 * s), wa.Bottom - Height));
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1500;
            timer.Tick += delegate { Close(); };
            timer.Start();
        }

        Font HintFont() { return new Font("Segoe UI", 13f * s, GraphicsUnit.Pixel); }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008 | 0x00000020;   // NOACTIVATE | TOOLWINDOW | TOPMOST | TRANSPARENT
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Panel);
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (var f = HintFont())
                TextRenderer.DrawText(e.Graphics, text, f, ClientRectangle, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { timer.Dispose(); base.OnFormClosed(e); }
    }

    // ------------------------------------------------------------------ Win32
    static class Native
    {
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
        [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO info);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    }

    // ------------------------------------------------------------------ crosshair cursor
    // Windows' own cross cursor is an even-sized image that gets scaled, so its visual centre falls
    // between two pixels. This one has a 1-pixel centre line exactly on the hotspot, so the
    // selection corner lines up with the crosshair perfectly.
    static class Crosshair
    {
        static Cursor cached;

        public static Bitmap Draw(int n)
        {
            int c = n / 2;
            var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (var dark = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))   // outline so it shows on white too
                {
                    g.FillRectangle(dark, c - 1, 1, 3, n - 2);
                    g.FillRectangle(dark, 1, c - 1, c - 2, 3);
                    g.FillRectangle(dark, c + 2, c - 1, n - c - 3, 3);
                }
                using (var w = new SolidBrush(Color.White))
                {
                    g.FillRectangle(w, c, 2, 1, n - 4);
                    g.FillRectangle(w, 2, c, n - 4, 1);
                }
            }
            return bmp;
        }

        public static Cursor Get()
        {
            if (cached != null) return cached;
            try
            {
                int n = Native.GetSystemMetrics(13 /* SM_CXCURSOR */);
                if (n < 16) n = 32;
                using (var bmp = Draw(n))
                {
                    IntPtr hIcon = bmp.GetHicon();
                    Native.ICONINFO ii;
                    Native.GetIconInfo(hIcon, out ii);
                    ii.fIcon = false;
                    ii.xHotspot = n / 2;
                    ii.yHotspot = n / 2;
                    IntPtr hCur = Native.CreateIconIndirect(ref ii);
                    Native.DeleteObject(ii.hbmColor);
                    Native.DeleteObject(ii.hbmMask);
                    Native.DestroyIcon(hIcon);
                    cached = hCur != IntPtr.Zero ? new Cursor(hCur) : Cursors.Cross;
                }
            }
            catch { cached = Cursors.Cross; }
            return cached;
        }
    }

    // ------------------------------------------------------------------ look & feel
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(32, 32, 32);
        public static readonly Color Panel = Color.FromArgb(44, 44, 44);
        public static readonly Color Button = Color.FromArgb(58, 58, 58);
        public static readonly Color Hover = Color.FromArgb(68, 68, 68);
        public static readonly Color Border = Color.FromArgb(85, 85, 85);
        public static readonly Color Text = Color.FromArgb(235, 235, 235);
        public static readonly Color Muted = Color.FromArgb(160, 160, 160);
        public static readonly Color Accent = Color.FromArgb(76, 194, 255);

        public static float Scale
        {
            get { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }
        }

        public static void DarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            try { if (Native.DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) Native.DwmSetWindowAttribute(hwnd, 19, ref on, 4); }
            catch { }
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Windows only lets the foreground app hand focus over; borrow the foreground thread's input for a moment.
        public static void ForceForeground(IntPtr hwnd)
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint fgThread = Native.GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint me = Native.GetCurrentThreadId();
            if (fgThread != 0 && fgThread != me)
            {
                Native.AttachThreadInput(me, fgThread, true);
                Native.BringWindowToTop(hwnd);
                Native.SetForegroundWindow(hwnd);
                Native.AttachThreadInput(me, fgThread, false);
            }
            else
            {
                Native.BringWindowToTop(hwnd);
                Native.SetForegroundWindow(hwnd);
            }
        }
    }

    class DarkColors : ProfessionalColorTable
    {
        static readonly Color Pressed = Color.FromArgb(80, 80, 80);
        static readonly Color Checked = Color.FromArgb(64, 64, 64);
        public override Color ToolStripGradientBegin { get { return Theme.Panel; } }
        public override Color ToolStripGradientMiddle { get { return Theme.Panel; } }
        public override Color ToolStripGradientEnd { get { return Theme.Panel; } }
        public override Color ToolStripBorder { get { return Theme.Panel; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.Hover; } }
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return Pressed; } }
        public override Color MenuItemPressedGradientMiddle { get { return Pressed; } }
        public override Color MenuItemPressedGradientEnd { get { return Pressed; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
        public override Color ButtonSelectedHighlight { get { return Theme.Hover; } }
        public override Color ButtonSelectedGradientBegin { get { return Theme.Hover; } }
        public override Color ButtonSelectedGradientMiddle { get { return Theme.Hover; } }
        public override Color ButtonSelectedGradientEnd { get { return Theme.Hover; } }
        public override Color ButtonSelectedBorder { get { return Theme.Hover; } }
        public override Color ButtonPressedGradientBegin { get { return Pressed; } }
        public override Color ButtonPressedGradientMiddle { get { return Pressed; } }
        public override Color ButtonPressedGradientEnd { get { return Pressed; } }
        public override Color ButtonPressedBorder { get { return Pressed; } }
        public override Color ButtonCheckedGradientBegin { get { return Checked; } }
        public override Color ButtonCheckedGradientMiddle { get { return Checked; } }
        public override Color ButtonCheckedGradientEnd { get { return Checked; } }
        public override Color ButtonCheckedHighlight { get { return Checked; } }
        public override Color CheckBackground { get { return Checked; } }
        public override Color CheckSelectedBackground { get { return Checked; } }
        public override Color CheckPressedBackground { get { return Checked; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Panel; } }
    }

    class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Color.FromArgb(110, 110, 110) : (e.Item.Tag as string == "on" ? Theme.Accent : Theme.Text);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Theme.Accent, 2f))
            {
                e.Graphics.DrawLines(p, new[] {
                    new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f),
                    new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.75f),
                    new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.28f) });
            }
        }
    }

    // ------------------------------------------------------------------ settings
    class Settings
    {
        public SnipMode Mode = SnipMode.Rectangle;
        public bool AutoSave = true;
        public string SaveDir = DefaultSaveDir;
        public bool WinShiftS = true;             // Win+Shift+S opens QuickSnip (replaces Snipping Tool's shortcut)
        public bool Border = false;               // thin grey border around image snips
        public bool OpenEditorAfterSnip = false;  // open the markup window after every snip
        public bool MultipleWindows = false;      // each snip gets its own markup window
        public bool AutoCopyEdits = true;         // markup changes go straight to the clipboard
        public bool AskToSave = true;             // ask before closing an edited snip
        public bool Magnifier = false;            // magnifier lens on the snip screen

        public static string DefaultSaveDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"); }
        }

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickSnip"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.txt"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    bool on = v == "1";
                    int m;
                    switch (k)
                    {
                        case "Mode": if (int.TryParse(v, out m) && m >= 0 && m <= 3) s.Mode = (SnipMode)m; break;
                        case "AutoSave": s.AutoSave = on; break;
                        case "SaveDir": if (v.Length > 0) s.SaveDir = v; break;
                        case "WinShiftS": s.WinShiftS = on; break;
                        case "Border": s.Border = on; break;
                        case "OpenEditorAfterSnip": s.OpenEditorAfterSnip = on; break;
                        case "MultipleWindows": s.MultipleWindows = on; break;
                        case "AutoCopyEdits": s.AutoCopyEdits = on; break;
                        case "AskToSave": s.AskToSave = on; break;
                        case "Magnifier": s.Magnifier = on; break;
                    }
                }
            }
            catch { }
            return s;
        }

        static string B(bool b) { return b ? "1" : "0"; }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[] {
                    "Mode=" + (int)Mode, "AutoSave=" + B(AutoSave), "SaveDir=" + SaveDir, "WinShiftS=" + B(WinShiftS),
                    "Border=" + B(Border), "OpenEditorAfterSnip=" + B(OpenEditorAfterSnip), "MultipleWindows=" + B(MultipleWindows),
                    "AutoCopyEdits=" + B(AutoCopyEdits), "AskToSave=" + B(AskToSave), "Magnifier=" + B(Magnifier) });
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ screen capture & clipboard
    static class Capture
    {
        public static Bitmap Screen(Rectangle vs)
        {
            var b = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
                g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy);
            return b;
        }

        // Visible top-level windows, top of the z-order first, in overlay (client) coordinates.
        public static List<Rectangle> Windows(Rectangle vs)
        {
            var list = new List<Rectangle>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                int cloaked;
                if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                int ex = Native.GetWindowLong(h, -20);
                if ((ex & 0x20) != 0 || (ex & 0x80) != 0) return true;           // click-through overlays, tool windows
                var sb = new StringBuilder(64);
                Native.GetClassName(h, sb, 64);
                string cls = sb.ToString();
                if (cls == "Progman" || cls == "WorkerW") return true;            // desktop -> falls back to the monitor
                Native.RECT r;
                if (Native.DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(Native.RECT))) != 0)
                    Native.GetWindowRect(h, out r);
                var rc = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                rc.Intersect(vs);
                if (rc.Width < 16 || rc.Height < 16) return true;
                rc.Offset(-vs.X, -vs.Y);
                list.Add(rc);
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }

    static class Clip
    {
        public static void Copy(Bitmap img)
        {
            var flat = new Bitmap(img.Width, img.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(flat))
            {
                g.Clear(Color.White);
                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
            }
            var png = new MemoryStream();
            img.Save(png, ImageFormat.Png);
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, true, flat);
            data.SetData("PNG", false, png);
            for (int i = 0; i < 10; i++)
            {
                try { Clipboard.SetDataObject(data, true); break; }
                catch (ExternalException) { Thread.Sleep(60); }
            }
            flat.Dispose();
        }

        public static void CopyText(string text)
        {
            for (int i = 0; i < 10; i++)
            {
                try { Clipboard.SetText(text, TextDataFormat.UnicodeText); break; }
                catch (ExternalException) { Thread.Sleep(60); }
            }
        }
    }

    // ------------------------------------------------------------------ modern "Select folder" dialog (Explorer style)
    static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attribs);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [In] ref Guid riid, out IShellItem item);

        // Returns the chosen folder, or null if cancelled.
        public static string Pick(IWin32Window owner, string startDir, string title)
        {
            try
            {
                var dlg = (IFileDialog)new FileOpenDialogRCW();
                dlg.SetOptions(0x20 | 0x40 | 0x800);   // PICKFOLDERS | FORCEFILESYSTEM | PATHMUSTEXIST
                dlg.SetTitle(title);
                dlg.SetOkButtonLabel("Save snips here");
                if (Directory.Exists(startDir))
                {
                    var iid = typeof(IShellItem).GUID;
                    IShellItem start;
                    SHCreateItemFromParsingName(startDir, IntPtr.Zero, ref iid, out start);
                    dlg.SetFolder(start);
                }
                if (dlg.Show(owner == null ? IntPtr.Zero : owner.Handle) != 0) return null;   // cancelled
                IShellItem result;
                dlg.GetResult(out result);
                string path;
                result.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out path);
                return path;
            }
            catch (Exception)
            {
                using (var fb = new FolderBrowserDialog())   // fallback: classic dialog
                {
                    fb.Description = title;
                    fb.SelectedPath = startDir;
                    return fb.ShowDialog(owner) == DialogResult.OK ? fb.SelectedPath : null;
                }
            }
        }
    }

    // ------------------------------------------------------------------ keyboard shortcuts (low-level hook)
    //   Print Screen              snip bar
    //   Ctrl+Print Screen         text extractor
    //   Shift+Print Screen        instant full-screen snip (monitor under the mouse)
    //   Ctrl+Shift+Print Screen   instant snip of the active window
    //   Win+Shift+S               snip bar (optional, replaces Snipping Tool's shortcut)
    //   Alt+ and Win+Print Screen are left to Windows.
    enum HotKey { SnipBar, TextExtract, FullScreenNow, WindowNow }

    class KeyHook : IDisposable
    {
        readonly Native.LowLevelKeyboardProc proc;
        readonly Control invoker;
        IntPtr hook;
        bool prtDown, sDown;
        public bool WinShiftS = true;
        public event Action<HotKey> Pressed;
        public bool Ok { get { return hook != IntPtr.Zero; } }

        public KeyHook(Control invoker)
        {
            this.invoker = invoker;
            proc = Callback;
            hook = Native.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, Native.GetModuleHandle(null), 0);
        }

        static bool Held(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

        void Fire(HotKey k)
        {
            var h = Pressed;
            if (h != null) invoker.BeginInvoke(h, k);   // never do real work inside the hook
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vk = Marshal.ReadInt32(lParam);
                int flags = Marshal.ReadInt32(lParam, 8);
                bool injected = (flags & 0x10) != 0;
                int msg = wParam.ToInt32();
                bool isDown = msg == 0x100 || msg == 0x104;
                bool win = Held(0x5B) || Held(0x5C), ctrl = Held(0x11), shift = Held(0x10), alt = (flags & 0x20) != 0;

                if (!injected && vk == 0x2C /* Print Screen */ && !win && !alt)
                {
                    HotKey k = ctrl && shift ? HotKey.WindowNow : ctrl ? HotKey.TextExtract : shift ? HotKey.FullScreenNow : HotKey.SnipBar;
                    if (isDown) { if (!prtDown) Fire(k); prtDown = true; }
                    else { if (!prtDown) Fire(k); prtDown = false; }   // some keyboards only send key-up
                    return (IntPtr)1;   // swallow so Windows doesn't open its own capture too
                }

                if (!injected && vk == 0x53 /* S */ && WinShiftS && (sDown || (win && shift && !ctrl && !alt)))
                {
                    if (isDown)
                    {
                        if (!sDown)
                        {
                            Fire(HotKey.SnipBar);
                            // a dummy key press stops the Start menu opening when Win is released
                            Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                            Native.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
                        }
                        sDown = true;
                    }
                    else sDown = false;
                    return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }
    }

    // ------------------------------------------------------------------ the snip overlay
    class Overlay : Form
    {
        readonly Bitmap shot, dim;
        readonly List<Rectangle> wins, mons;
        readonly float s;
        SnipMode mode;
        Rectangle sel, hover;
        bool dragging, shown, finished;
        Point start;
        readonly List<Point> path = new List<Point>();
        Rectangle bar;
        readonly Rectangle[] btns = new Rectangle[9];   // 0-5 snip modes, 6 magnifier, 7 save folder, 8 close
        const int MagBtn = 6, FolderBtn = 7, CloseBtn = 8, ModeCount = 6;
        int hoverBtn = -1;
        readonly Rectangle primary;
        bool picking;
        static readonly string[] Labels = { "Rectangle", "Window", "Full screen", "Freeform", "Text extractor - copies the text, not a picture", "Color picker - click to copy a color code", null, null, "Close (Esc)" };

        public Bitmap Result;
        public SnipMode Mode { get { return mode; } }
        bool IsDragMode { get { return mode == SnipMode.Rectangle || mode == SnipMode.Text; } }

        // ---- color picker
        Point cur = new Point(-1, -1);       // mouse position (for the magnifier)
        public Color? PickedColor;

        public static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        Color PixelAt(Point p)
        {
            int x = Math.Max(0, Math.Min(shot.Width - 1, p.X)), y = Math.Max(0, Math.Min(shot.Height - 1, p.Y));
            var c = shot.GetPixel(x, y);
            return Color.FromArgb(255, c.R, c.G, c.B);
        }

        Rectangle LoupeRect(Point p)
        {
            if (p.X < 0 || mode != SnipMode.ColorPicker) return Rectangle.Empty;
            int L = (int)(132 * s), lab = (int)(30 * s), off = (int)(22 * s);
            int x = p.X + off, y = p.Y + off;
            if (x + L > shot.Width) x = p.X - off - L;
            if (y + L + lab > shot.Height) y = p.Y - off - L - lab;
            return new Rectangle(x, y, L, L + lab);
        }

        void DrawLoupe(Graphics g)
        {
            var r = LoupeRect(cur);
            if (!Valid(r)) return;
            const int n = 11;                       // 11 x 11 pixels around the cursor
            int L = r.Width;
            float cell = L / (float)n;
            using (var b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, r);
            var im = g.InterpolationMode; var po = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(shot, new Rectangle(r.X, r.Y, L, L), new Rectangle(cur.X - n / 2, cur.Y - n / 2, n, n), GraphicsUnit.Pixel);
            g.InterpolationMode = im; g.PixelOffsetMode = po;
            float cx = r.X + (n / 2) * cell, cy = r.Y + (n / 2) * cell;
            using (var p1 = new Pen(Color.Black, 3)) g.DrawRectangle(p1, cx, cy, cell, cell);
            using (var p2 = new Pen(Color.White, 1)) g.DrawRectangle(p2, cx, cy, cell, cell);

            var c = PixelAt(cur);
            var lab = new Rectangle(r.X, r.Y + L, L, r.Height - L);
            int pad = (int)(7 * s);
            var sw = new Rectangle(lab.X + pad, lab.Y + pad, lab.Height - pad * 2, lab.Height - pad * 2);
            using (var b = new SolidBrush(c)) g.FillRectangle(b, sw);
            using (var p = new Pen(Theme.Border)) { g.DrawRectangle(p, sw); g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1); }
            using (var f = BarFont())
                TextRenderer.DrawText(g, Hex(c), f, new Rectangle(sw.Right + pad, lab.Y, lab.Right - sw.Right - pad, lab.Height), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        void MoveLoupe(Point p)
        {
            var olds = new[] { LoupeRect(cur), MagRect(cur) };
            cur = p;
            var nows = new[] { LoupeRect(cur), MagRect(cur) };
            foreach (var r in olds) if (Valid(r)) { var q = r; q.Inflate(2, 2); Dirty(q); }
            foreach (var r in nows) if (Valid(r)) { var q = r; q.Inflate(2, 2); Dirty(q); }
            Update();
        }

        // ---- magnifier: round lens that follows the cursor (all modes except the color picker, which has its own)
        public bool MagnifierOn;
        Point screenOrigin;   // top-left of the virtual screen, for real screen coordinates

        void ToggleMagnifier()
        {
            MagnifierOn = !MagnifierOn;
            DirtyAll();
        }

        const int MagN = 17;   // pixels shown across the lens (odd, so there is a true centre pixel)

        // Size of one enlarged pixel in the lens: a whole, odd number of screen pixels,
        // so the centre pixel has an exact middle for the crosshair.
        int MagCell
        {
            get
            {
                int c = (int)Math.Round(9 * s);
                return c % 2 == 0 ? c + 1 : c;
            }
        }

        // for --selftest only
        public Rectangle TestMagCircle(Point p) { return MagCircle(p); }
        public int TestMagCell { get { return MagCell; } }

        Rectangle MagCircle(Point p)
        {
            int D = MagCell * MagN, off = (int)(26 * s);
            int x = p.X + off, y = p.Y + off;
            if (x + D > shot.Width) x = p.X - off - D;
            if (y + D > shot.Height) y = p.Y - off - D;
            return new Rectangle(x, y, D, D);
        }

        string MagText
        {
            get
            {
                if (dragging && IsDragMode && Valid(sel)) return sel.Width + " x " + sel.Height;
                return (cur.X + screenOrigin.X) + ", " + (cur.Y + screenOrigin.Y);
            }
        }

        Rectangle MagLabel(Rectangle c)
        {
            int h = (int)(22 * s), gap = (int)(4 * s);
            int y = c.Top - h - gap;
            if (y < 0) y = c.Bottom + gap;
            return new Rectangle(c.X, y, c.Width, h);
        }

        Rectangle MagRect(Point p)
        {
            if (!MagnifierOn || p.X < 0 || mode == SnipMode.ColorPicker) return Rectangle.Empty;
            var c = MagCircle(p);
            var r = Rectangle.Union(c, MagLabel(c));
            r.Inflate(3, 3);
            return r;
        }

        void DrawMagnifier(Graphics g)
        {
            var c = MagCircle(cur);
            const int n = MagN;
            int cell = MagCell;                       // whole screen pixels per enlarged pixel
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(c);
                var st = g.Save();
                g.SetClip(gp, CombineMode.Intersect);
                g.SmoothingMode = SmoothingMode.None;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(shot, c, new Rectangle(cur.X - n / 2, cur.Y - n / 2, n, n), GraphicsUnit.Pixel);
                g.PixelOffsetMode = PixelOffsetMode.Default;

                // Crosshair drawn as whole-pixel bars: odd thickness, centred exactly on the middle of the
                // centre pixel, with the same 1-pixel gap on all four sides of it.
                int x0 = c.X + (n / 2) * cell, y0 = c.Y + (n / 2) * cell;   // centre pixel's cell
                int x1 = x0 + cell, y1 = y0 + cell;                         // (exclusive)
                int mx = x0 + cell / 2, my = y0 + cell / 2;                 // its exact middle
                int t = (int)Math.Round(2 * s); if (t % 2 == 0) t++;        // arm thickness, odd
                int h = t / 2;
                var arms = new[] {
                    Rectangle.FromLTRB(c.Left, my - h, x0 - 1, my + h + 1),      // left
                    Rectangle.FromLTRB(x1 + 1, my - h, c.Right, my + h + 1),     // right
                    Rectangle.FromLTRB(mx - h, c.Top, mx + h + 1, y0 - 1),       // top
                    Rectangle.FromLTRB(mx - h, y1 + 1, mx + h + 1, c.Bottom) };  // bottom
                using (var white = new SolidBrush(Color.White))
                using (var black = new SolidBrush(Color.Black))
                {
                    foreach (var a in arms) g.FillRectangle(white, Rectangle.Inflate(a, 1, 1));   // solid 1 px white outline
                    foreach (var a in arms) g.FillRectangle(black, a);                            // solid black arm
                }
                g.Restore(st);
            }
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var ring = new Pen(Color.FromArgb(225, 225, 225), 2f * s)) g.DrawEllipse(ring, c);
            g.SmoothingMode = sm;

            // position (or selection size while dragging)
            var lab = MagLabel(c);
            using (var f = BarFont())
            {
                string t = MagText;
                var sz = TextRenderer.MeasureText(g, t, f);
                var box = new Rectangle(lab.X, lab.Y, Math.Min(lab.Width, sz.Width + (int)(14 * s)), lab.Height);
                using (var b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, box);
                using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, box.X, box.Y, box.Width - 1, box.Height - 1);
                TextRenderer.DrawText(g, t, f, box, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // for --selftest only: the first paint of a freshly opened snip screen, with the lens already
        // positioned (what happens when it opens with the magnifier on). Returns pixels that differ from
        // a full draw; the old code left everything except the lens black.
        public int TestFirstPaintDiff(Point lensAt)
        {
            everPainted = false; dirtyAll = false; dirty.Clear();
            cur = new Point(-1, -1);
            var r = MagRect(lensAt);   // what MoveLoupe marks as changed
            cur = lensAt;
            if (Valid(r)) { r.Inflate(2, 2); dirty.Add(r); }
            var full = new Rectangle(0, 0, shot.Width, shot.Height);
            using (var first = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppArgb))
            using (var expect = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(first)) { g.Clear(Color.Black); PaintNow(g, full); }   // Windows asks for the whole window
                dirty.Clear();
                using (var g = Graphics.FromImage(expect)) Render(g, full);
                return CountDiff(first, expect);
            }
        }

        // for --selftest only
        public void SetTestCursor(Point p) { cur = p; }
        public void SetTestDragging(bool d) { dragging = d; }
        public string SaveDir { get; private set; }
        public bool AutoSave { get; private set; }

        public Overlay(Bitmap shot, Rectangle vs, SnipMode mode, List<Rectangle> wins, List<Rectangle> mons, Rectangle primary,
                       string saveDir, bool autoSave, float scale)
        {
            this.shot = shot; this.wins = wins; this.mons = mons; this.mode = mode; this.primary = primary; s = scale;
            SaveDir = saveDir; AutoSave = autoSave;
            screenOrigin = vs.Location;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = vs;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Cursor = Crosshair.Get();
            BackColor = Color.Black;
            Text = "QuickSnip";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Opaque, true);

            dim = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(dim))
            {
                g.DrawImage(shot, new Rectangle(0, 0, shot.Width, shot.Height));
                using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(b, 0, 0, dim.Width, dim.Height);
            }

            LayoutBar();
        }

        Font BarFont() { return new Font("Segoe UI", 12f * s, GraphicsUnit.Pixel); }

        // Short name shown on the folder button, e.g. "Screenshots" or "E:\".
        string FolderText
        {
            get
            {
                if (!AutoSave) return "Not saving";
                string n = Path.GetFileName(SaveDir.TrimEnd('\\'));
                if (string.IsNullOrEmpty(n)) n = SaveDir;
                if (n.Length > 22) n = n.Substring(0, 21) + "...";
                return n;
            }
        }

        string FolderLabel
        {
            get { return AutoSave ? "Saving to " + SaveDir + "   -   click to change" : "Auto-save is off   -   click to pick a folder"; }
        }

        void LayoutBar()
        {
            int bs = (int)(40 * s), pad = (int)(6 * s), gap = (int)(2 * s), sep = (int)(12 * s);
            int tw;
            using (var f = BarFont()) tw = TextRenderer.MeasureText(FolderText, f).Width;
            int cw = (int)(32 * s) + tw + (int)(12 * s);
            int w = pad * 2 + bs * ModeCount + gap * (ModeCount - 1) + sep + bs + sep + cw + sep + bs;
            bar = new Rectangle(primary.X + (primary.Width - w) / 2, primary.Y + (int)(16 * s), w, bs + pad * 2);
            int x = bar.X + pad, y = bar.Y + pad;
            for (int i = 0; i < ModeCount; i++) { btns[i] = new Rectangle(x, y, bs, bs); x += bs + gap; }
            x += sep - gap;
            btns[MagBtn] = new Rectangle(x, y, bs, bs);
            x += bs + sep;
            btns[FolderBtn] = new Rectangle(x, y, cw, bs);
            x += cw + sep;
            btns[CloseBtn] = new Rectangle(x, y, bs, bs);
        }

        void PickFolder()
        {
            picking = true;
            string chosen = null;
            try { chosen = FolderPicker.Pick(this, SaveDir, "Choose where QuickSnip saves your snips"); }
            finally { picking = false; }
            Theme.ForceForeground(Handle);
            Activate();
            if (chosen == null) return;
            SaveDir = chosen;
            AutoSave = true;
            hoverBtn = -1;
            LayoutBar();
            DirtyAll();
        }

        // for --selftest only: drag through the steps using only incremental repaints and
        // return how many pixels differ from a clean full repaint (must be 0).
        public int TestIncrementalDiff(Rectangle[] steps)
        {
            mode = SnipMode.Rectangle; hoverBtn = -1; sel = Rectangle.Empty; dirty.Clear();
            var full = new Rectangle(0, 0, shot.Width, shot.Height);
            using (var inc = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppArgb))
            using (var exp = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(inc)) Render(g, full);
                foreach (var step in steps)
                {
                    var old = sel; sel = step; dirty.Clear();
                    InvalidateSelection(old, sel);
                    using (var g = Graphics.FromImage(inc))
                        foreach (var d in dirty)
                        {
                            var r = Rectangle.Intersect(d, full);
                            if (!Valid(r)) continue;
                            var gs = g.Save(); g.SetClip(r); Render(g, r); g.Restore(gs);
                        }
                }
                dirty.Clear();
                using (var g = Graphics.FromImage(exp)) Render(g, full);
                return CountDiff(inc, exp);
            }
        }

        static int CountDiff(Bitmap a, Bitmap b)
        {
            var r = new Rectangle(0, 0, a.Width, a.Height);
            var da = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var db = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var pa = new int[a.Width * a.Height];
            var pb = new int[a.Width * a.Height];
            Marshal.Copy(da.Scan0, pa, 0, pa.Length);
            Marshal.Copy(db.Scan0, pb, 0, pb.Length);
            a.UnlockBits(da); b.UnlockBits(db);
            int n = 0;
            for (int i = 0; i < pa.Length; i++) if (pa[i] != pb[i]) n++;
            return n;
        }

        // for --selftest only
        public void SetTestState(SnipMode m, Rectangle selection, Rectangle hov, int hb, Point[] freeform)
        {
            mode = m; sel = selection; hover = hov; hoverBtn = hb; dragging = false;
            path.Clear();
            if (freeform != null) path.AddRange(freeform);
        }

        static bool Valid(Rectangle r) { return r.Width > 0 && r.Height > 0; }

        Rectangle BarZone
        {
            get { return Rectangle.FromLTRB(primary.Left, primary.Top, primary.Right, bar.Bottom + (int)(48 * s)); }
        }

        // ---- repaint bookkeeping: only the exact areas that changed get redrawn,
        // so the selection keeps up with the mouse even on large / high-res screens.
        readonly List<Rectangle> dirty = new List<Rectangle>();
        bool dirtyAll;

        void Dirty(Rectangle r) { if (!Valid(r)) return; dirty.Add(r); Invalidate(r); }
        void DirtyAll() { dirtyAll = true; Invalidate(); }

        void InvalidateBoth(Rectangle a, Rectangle b)
        {
            Rectangle u;
            if (!Valid(a)) u = b; else if (!Valid(b)) u = a; else u = Rectangle.Union(a, b);
            if (!Valid(u)) return;
            int k = (int)(3 * s) + 2;
            u.Inflate(k, k);
            Dirty(u);
        }

        // r minus hole, as up to 4 rectangles
        static List<Rectangle> Subtract(Rectangle r, Rectangle hole)
        {
            var list = new List<Rectangle>();
            var h = Rectangle.Intersect(r, hole);
            if (!Valid(h)) { list.Add(r); return list; }
            if (h.Top > r.Top) list.Add(Rectangle.FromLTRB(r.Left, r.Top, r.Right, h.Top));
            if (h.Bottom < r.Bottom) list.Add(Rectangle.FromLTRB(r.Left, h.Bottom, r.Right, r.Bottom));
            if (h.Left > r.Left) list.Add(Rectangle.FromLTRB(r.Left, h.Top, h.Left, h.Bottom));
            if (h.Right < r.Right) list.Add(Rectangle.FromLTRB(h.Right, h.Top, r.Right, h.Bottom));
            return list;
        }

        // Selection moved from a to b: redraw only the strips that differ (plus both borders).
        void InvalidateSelection(Rectangle a, Rectangle b)
        {
            if (!Valid(a) || !Valid(b)) { InvalidateBoth(a, b); return; }
            int k = (int)(3 * s) + 2;
            var inner = Rectangle.Intersect(a, b);
            inner.Inflate(-k, -k);
            var oa = a; oa.Inflate(k, k);
            var ob = b; ob.Inflate(k, k);
            foreach (var r in Subtract(oa, inner)) Dirty(r);
            foreach (var r in Subtract(ob, inner)) Dirty(r);
        }

        protected override void OnPaint(PaintEventArgs e) { PaintNow(e.Graphics, e.ClipRectangle); }

        bool everPainted;

        // Draws only our own small changed areas when that's all Windows asked for. Anything else
        // (the very first paint, a window moved away, etc.) gets the whole requested area drawn.
        void PaintNow(Graphics g, Rectangle clip)
        {
            bool onlyOurs = everPainted && !dirtyAll && dirty.Count > 0;
            if (onlyOurs)
            {
                var ours = dirty[0];
                foreach (var d in dirty) ours = Rectangle.Union(ours, d);
                onlyOurs = ours.Contains(clip);   // Windows wants more than we changed -> draw it all
            }
            everPainted = true;
            if (!onlyOurs) Render(g, clip);
            else
            {
                foreach (var d in dirty)
                {
                    var r = Rectangle.Intersect(d, clip);
                    if (!Valid(r)) continue;
                    var st = g.Save();
                    g.SetClip(r);
                    Render(g, r);
                    g.Restore(st);
                }
            }
            dirty.Clear();
            dirtyAll = false;
        }

        public void Render(Graphics g, Rectangle clip)
        {
            g.DrawImage(dim, clip, clip, GraphicsUnit.Pixel);
            if (mode == SnipMode.Freeform)
            {
                if (path.Count > 1)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var pen = new Pen(Color.White, Math.Max(1.5f, 2f * s)))
                    {
                        if (dragging || path.Count < 3) g.DrawLines(pen, path.ToArray());
                        else
                        {
                            using (var gp = new GraphicsPath())
                            {
                                gp.AddPolygon(path.ToArray());
                                var st = g.Save();
                                g.SetClip(gp, CombineMode.Intersect);
                                g.DrawImage(shot, clip, clip, GraphicsUnit.Pixel);
                                g.Restore(st);
                                g.DrawPath(pen, gp);
                            }
                        }
                    }
                    g.SmoothingMode = SmoothingMode.None;
                }
            }
            else
            {
                Rectangle h = IsDragMode ? sel : hover;
                if (Valid(h))
                {
                    var r = Rectangle.Intersect(h, clip);
                    if (Valid(r)) g.DrawImage(shot, r, r, GraphicsUnit.Pixel);
                    if (IsDragMode)
                    {
                        // selection: thin white dashes over a faint dark line (stays visible on white too)
                        using (var under = new Pen(Color.FromArgb(150, 0, 0, 0), 1f))
                            g.DrawRectangle(under, h.X, h.Y, h.Width - 1, h.Height - 1);
                        using (var white = new SolidBrush(Color.White))
                        {
                            DashedH(g, white, h.Left, h.Right, h.Top);
                            DashedH(g, white, h.Left, h.Right, h.Bottom - 1);
                            DashedV(g, white, h.Top, h.Bottom, h.Left);
                            DashedV(g, white, h.Top, h.Bottom, h.Right - 1);
                        }
                    }
                    else
                    {
                        using (var pen = new Pen(Theme.Accent, Math.Max(1f, 2f * s)))
                        {
                            pen.Alignment = PenAlignment.Inset;
                            g.DrawRectangle(pen, h.X, h.Y, h.Width - 1, h.Height - 1);
                        }
                    }
                }
            }
            if (!dragging && clip.IntersectsWith(BarZone)) DrawBar(g);   // bar is hidden while a selection is being dragged
            if (mode == SnipMode.ColorPicker && clip.IntersectsWith(LoupeRect(cur))) DrawLoupe(g);
            if (MagnifierOn && clip.IntersectsWith(MagRect(cur))) DrawMagnifier(g);
        }

        // Dashes are pinned to fixed screen positions (every 7 px: 4 on, 3 off), so they stay still
        // while the box is resized instead of crawling along the edges.
        const int DashOn = 4, DashPeriod = 7;
        static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

        static void DashedH(Graphics g, Brush b, int x0, int x1, int y)   // x1 exclusive
        {
            for (int x = x0 - Mod(x0, DashPeriod); x < x1; x += DashPeriod)
            {
                int a = Math.Max(x, x0), e = Math.Min(x + DashOn, x1);
                if (e > a) g.FillRectangle(b, a, y, e - a, 1);
            }
        }

        static void DashedV(Graphics g, Brush b, int y0, int y1, int x)   // y1 exclusive
        {
            for (int y = y0 - Mod(y0, DashPeriod); y < y1; y += DashPeriod)
            {
                int a = Math.Max(y, y0), e = Math.Min(y + DashOn, y1);
                if (e > a) g.FillRectangle(b, x, a, 1, e - a);
            }
        }

        void DrawBar(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var gp = Theme.Rounded(bar, 8 * s))
            {
                using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, gp);
                using (var p = new Pen(Theme.Border)) g.DrawPath(p, gp);
            }
            for (int i = 0; i < 9; i++)
            {
                var r = btns[i];
                bool on = (i < ModeCount && (int)mode == i) || (i == MagBtn && MagnifierOn);
                if (on || i == hoverBtn)
                {
                    using (var gp = Theme.Rounded(r, 5 * s))
                    using (var b = new SolidBrush(i == hoverBtn ? Theme.Hover : Theme.Button))
                        g.FillPath(b, gp);
                }
                if (on)
                    using (var b = new SolidBrush(Theme.Accent))
                        g.FillRectangle(b, r.X + r.Width / 2f - 8 * s, r.Bottom - 3 * s, 16 * s, 2 * s);
                if (i == FolderBtn) DrawFolderButton(g, r);
                else DrawIcon(g, i == CloseBtn ? 6 : i == MagBtn ? 7 : i, r, on);
            }
            foreach (int k in new[] { MagBtn, FolderBtn, CloseBtn })
            {
                float sx = btns[k].X - 6 * s;
                using (var p = new Pen(Theme.Border)) g.DrawLine(p, sx, bar.Y + 10 * s, sx, bar.Bottom - 10 * s);
            }

            if (hoverBtn >= 0)
            {
                using (var f = BarFont())
                {
                    string t = hoverBtn == FolderBtn ? FolderLabel : hoverBtn == MagBtn ? (MagnifierOn ? "Magnifier: on (M)" : "Magnifier: off (M)") : Labels[hoverBtn];
                    var sz = TextRenderer.MeasureText(g, t, f);
                    var b = btns[hoverBtn];
                    int lw = sz.Width + (int)(16 * s);
                    int lx = b.X + b.Width / 2 - lw / 2;
                    lx = Math.Max(primary.Left + 4, Math.Min(lx, primary.Right - lw - 4));   // keep long paths on screen
                    var lr = new Rectangle(lx, bar.Bottom + (int)(6 * s), lw, sz.Height + (int)(8 * s));
                    using (var gp = Theme.Rounded(lr, 4 * s))
                    {
                        using (var br = new SolidBrush(Theme.Panel)) g.FillPath(br, gp);
                        using (var p = new Pen(Theme.Border)) g.DrawPath(p, gp);
                    }
                    TextRenderer.DrawText(g, t, f, lr, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        void DrawFolderButton(Graphics g, Rectangle r)
        {
            float u = s, ix = r.X + 10 * u, cy = r.Y + r.Height / 2f;
            using (var p = new Pen(AutoSave ? Theme.Text : Theme.Muted, 1.5f * u))
            using (var gp = new GraphicsPath())
            {
                p.LineJoin = LineJoin.Round;
                gp.AddLines(new[] {
                    new PointF(ix, cy - 6 * u), new PointF(ix + 6 * u, cy - 6 * u), new PointF(ix + 8 * u, cy - 4 * u),
                    new PointF(ix + 16 * u, cy - 4 * u), new PointF(ix + 16 * u, cy + 6 * u), new PointF(ix, cy + 6 * u) });
                gp.CloseFigure();
                g.DrawPath(p, gp);
            }
            using (var f = BarFont())
                TextRenderer.DrawText(g, FolderText, f, new Rectangle(r.X + (int)(32 * u), r.Y, r.Width - (int)(32 * u), r.Height),
                    AutoSave ? Theme.Text : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        void DrawIcon(Graphics g, int i, Rectangle r, bool on)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = s;
            using (var p = new Pen(on ? Theme.Accent : Theme.Text, 1.6f * u))
            {
                p.LineJoin = LineJoin.Round;
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                switch (i)
                {
                    case 0:
                        p.DashStyle = DashStyle.Dash;
                        g.DrawRectangle(p, cx - 9 * u, cy - 7 * u, 18 * u, 14 * u);
                        break;
                    case 1:
                        g.DrawRectangle(p, cx - 9 * u, cy - 7 * u, 18 * u, 14 * u);
                        g.DrawLine(p, cx - 9 * u, cy - 3 * u, cx + 9 * u, cy - 3 * u);
                        break;
                    case 2:
                        g.DrawRectangle(p, cx - 9 * u, cy - 8 * u, 18 * u, 12 * u);
                        g.DrawLine(p, cx, cy + 4 * u, cx, cy + 7 * u);
                        g.DrawLine(p, cx - 5 * u, cy + 8 * u, cx + 5 * u, cy + 8 * u);
                        break;
                    case 3:
                        using (var gp = new GraphicsPath())
                        {
                            gp.AddBezier(cx - 8 * u, cy + 2 * u, cx - 10 * u, cy - 9 * u, cx + 4 * u, cy - 10 * u, cx + 8 * u, cy - 3 * u);
                            gp.AddBezier(cx + 8 * u, cy - 3 * u, cx + 11 * u, cy + 3 * u, cx + 2 * u, cy + 9 * u, cx - 3 * u, cy + 6 * u);
                            gp.AddBezier(cx - 3 * u, cy + 6 * u, cx - 6 * u, cy + 5 * u, cx - 7 * u, cy + 4 * u, cx - 8 * u, cy + 2 * u);
                            g.DrawPath(p, gp);
                        }
                        break;
                    case 4:   // text extractor: a "T" inside selection corners
                        float a = 9 * u, b = 4 * u;
                        g.DrawLines(p, new[] { new PointF(cx - a, cy - a + b), new PointF(cx - a, cy - a), new PointF(cx - a + b, cy - a) });
                        g.DrawLines(p, new[] { new PointF(cx + a - b, cy - a), new PointF(cx + a, cy - a), new PointF(cx + a, cy - a + b) });
                        g.DrawLines(p, new[] { new PointF(cx - a, cy + a - b), new PointF(cx - a, cy + a), new PointF(cx - a + b, cy + a) });
                        g.DrawLines(p, new[] { new PointF(cx + a - b, cy + a), new PointF(cx + a, cy + a), new PointF(cx + a, cy + a - b) });
                        g.DrawLine(p, cx - 4 * u, cy - 4 * u, cx + 4 * u, cy - 4 * u);
                        g.DrawLine(p, cx, cy - 4 * u, cx, cy + 5 * u);
                        break;
                    case 5:   // color picker: eyedropper
                        g.DrawLine(p, cx - 7 * u, cy + 7 * u, cx + 2 * u, cy - 2 * u);
                        g.DrawEllipse(p, cx + 1 * u, cy - 8 * u, 7 * u, 7 * u);
                        g.DrawLine(p, cx - 1 * u, cy - 5 * u, cx + 5 * u, cy + 1 * u);
                        break;
                    case 7:   // magnifier
                        g.DrawEllipse(p, cx - 8 * u, cy - 8 * u, 11 * u, 11 * u);
                        g.DrawLine(p, cx + 1.5f * u, cy + 1.5f * u, cx + 7 * u, cy + 7 * u);
                        break;
                    case 6:
                        g.DrawLine(p, cx - 6 * u, cy - 6 * u, cx + 6 * u, cy + 6 * u);
                        g.DrawLine(p, cx + 6 * u, cy - 6 * u, cx - 6 * u, cy + 6 * u);
                        break;
                }
            }
        }

        int HitButton(Point pt)
        {
            for (int i = 0; i < 9; i++) if (btns[i].Contains(pt)) return i;
            return -1;
        }

        Rectangle MonitorAt(Point pt)
        {
            foreach (var m in mons) if (m.Contains(pt)) return m;
            return mons.Count > 0 ? mons[0] : Rectangle.Empty;
        }

        void UpdateHover(Point pt)
        {
            Rectangle nh = Rectangle.Empty;
            if (mode == SnipMode.Window)
            {
                foreach (var w in wins) if (w.Contains(pt)) { nh = w; break; }
                if (!Valid(nh)) nh = MonitorAt(pt);
            }
            else if (mode == SnipMode.Fullscreen) nh = MonitorAt(pt);
            if (nh != hover) { var old = hover; hover = nh; InvalidateBoth(old, hover); }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.ForceForeground(Handle);
            Activate();
            shown = true;
            var p = PointToClient(Cursor.Position);
            UpdateHover(p);
            MoveLoupe(bar.Contains(p) ? new Point(-1, -1) : p);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (shown && !finished && !picking) Cancel();   // alt-tabbed away (but not while choosing a folder)
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Cancel();
            else if (e.KeyCode == Keys.M) ToggleMagnifier();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Cancel(); return; }
            if (e.Button != MouseButtons.Left) return;
            int b = HitButton(e.Location);
            if (b == CloseBtn) { Cancel(); return; }
            if (b == FolderBtn) { PickFolder(); return; }
            if (b == MagBtn) { ToggleMagnifier(); return; }
            if (b >= 0)
            {
                mode = (SnipMode)b;
                sel = Rectangle.Empty; hover = Rectangle.Empty; path.Clear();
                DirtyAll();
                return;
            }
            if (bar.Contains(e.Location)) return;
            switch (mode)
            {
                case SnipMode.Rectangle:
                case SnipMode.Text:
                    dragging = true; start = e.Location; sel = new Rectangle(e.Location, Size.Empty);
                    hoverBtn = -1;
                    Dirty(BarZone);   // hide the bar
                    break;
                case SnipMode.Freeform:
                    dragging = true; path.Clear(); path.Add(e.Location); hoverBtn = -1; DirtyAll();
                    break;
                case SnipMode.ColorPicker:
                    PickedColor = PixelAt(e.Location);
                    finished = true;
                    DialogResult = DialogResult.OK;
                    Close();
                    break;
                default:
                    UpdateHover(e.Location);
                    if (Valid(hover)) Finish(Crop(shot, hover));
                    break;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int b = HitButton(e.Location);
            if (b != hoverBtn) { hoverBtn = b; Dirty(BarZone); }
            var want = (!dragging && bar.Contains(e.Location)) ? Cursors.Default : Crosshair.Get();
            if (Cursor != want) Cursor = want;

            var lensAt = (!dragging && bar.Contains(e.Location)) ? new Point(-1, -1) : e.Location;   // no lens over the bar
            if (mode == SnipMode.ColorPicker) { MoveLoupe(lensAt); return; }
            if (dragging && IsDragMode)
            {
                var old = sel;
                // +1 so the pixel under the cursor is inside the box: the corner sits exactly on the crosshair
                sel = Rectangle.FromLTRB(Math.Min(start.X, e.X), Math.Min(start.Y, e.Y), Math.Max(start.X, e.X) + 1, Math.Max(start.Y, e.Y) + 1);
                InvalidateSelection(old, sel);
            }
            else if (dragging && mode == SnipMode.Freeform)
            {
                var last = path[path.Count - 1];
                path.Add(e.Location);
                InvalidateBoth(Rectangle.FromLTRB(Math.Min(last.X, e.X), Math.Min(last.Y, e.Y), Math.Max(last.X, e.X) + 1, Math.Max(last.Y, e.Y) + 1), Rectangle.Empty);
            }
            else if (!dragging) UpdateHover(e.Location);
            MoveLoupe(lensAt);   // moves the magnifier (if on) and paints now, so everything tracks the cursor
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !dragging) return;
            dragging = false;
            Dirty(BarZone);   // bring the bar back (if the snip is cancelled)
            if (IsDragMode)
            {
                if (sel.Width > 3 && sel.Height > 3) Finish(Crop(shot, sel));
                else { var old = sel; sel = Rectangle.Empty; InvalidateBoth(old, Rectangle.Empty); }
            }
            else if (mode == SnipMode.Freeform)
            {
                Bitmap r = path.Count > 3 ? CropPath(shot, path.ToArray()) : null;
                if (r != null) Finish(r); else { path.Clear(); DirtyAll(); }
            }
        }

        void Finish(Bitmap result)
        {
            finished = true;
            Result = result;
            DialogResult = DialogResult.OK;
            Close();
        }

        void Cancel()
        {
            finished = true;
            Result = null;
            DialogResult = DialogResult.Cancel;
            Close();
        }

        public static Bitmap Crop(Bitmap src, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, src.Width, src.Height));
            if (r.Width < 1 || r.Height < 1) return null;
            var b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
                g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            return b;
        }

        public static Bitmap CropPath(Bitmap src, Point[] pts)
        {
            using (var gp = new GraphicsPath())
            {
                gp.AddPolygon(pts);
                var bounds = Rectangle.Round(gp.GetBounds());
                bounds.Intersect(new Rectangle(0, 0, src.Width, src.Height));
                if (bounds.Width < 2 || bounds.Height < 2) return null;
                var b = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(b))
                {
                    g.Clear(Color.White);
                    using (var m = new Matrix()) { m.Translate(-bounds.X, -bounds.Y); gp.Transform(m); }
                    g.SetClip(gp);
                    g.DrawImage(src, new Rectangle(0, 0, bounds.Width, bounds.Height), bounds, GraphicsUnit.Pixel);
                }
                return b;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) dim.Dispose();
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ markup window
    enum ItemKind { Pen, Highlighter, Line, Arrow, Rect, Oval, Redact }

    // One mark on the snip. Items are never changed after they're finished (crop makes shifted copies),
    // so undo can simply keep old lists.
    class Item
    {
        public ItemKind Kind;
        public Color Color;
        public float Width;
        public readonly List<Point> Pts = new List<Point>();   // pen path, or the two end points of a shape
        public Rectangle Box;                                  // redaction box

        public bool IsStroke { get { return Kind == ItemKind.Pen || Kind == ItemKind.Highlighter; } }

        public Item Shifted(int dx, int dy)
        {
            var c = new Item();
            c.Kind = Kind; c.Color = Color; c.Width = Width;
            foreach (var p in Pts) c.Pts.Add(new Point(p.X + dx, p.Y + dy));
            c.Box = Box;
            c.Box.Offset(dx, dy);
            return c;
        }

        Rectangle Norm()
        {
            var a = Pts[0]; var b = Pts[Pts.Count - 1];
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        float HeadLength { get { return Width * 3.5f + 8f; } }

        public Rectangle Bounds()
        {
            if (Kind == ItemKind.Redact) return Box;
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            foreach (var p in Pts) { l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y); }
            int k = (int)Math.Ceiling(Width + (Kind == ItemKind.Arrow ? HeadLength : 0)) + 2;
            return Rectangle.FromLTRB(l - k, t - k, r + k, b + k);
        }

        GraphicsPath Outline()
        {
            var gp = new GraphicsPath();
            switch (Kind)
            {
                case ItemKind.Pen:
                case ItemKind.Highlighter:
                    if (Pts.Count == 1) gp.AddEllipse(Pts[0].X - Width / 2, Pts[0].Y - Width / 2, Width, Width);
                    else gp.AddLines(Pts.ToArray());
                    break;
                case ItemKind.Line:
                case ItemKind.Arrow:
                    gp.AddLine(Pts[0], Pts[Pts.Count - 1]);
                    break;
                case ItemKind.Rect:
                    gp.AddRectangle(Norm());
                    break;
                case ItemKind.Oval:
                    var r = Norm();
                    if (r.Width > 0 && r.Height > 0) gp.AddEllipse(r); else gp.AddLine(Pts[0], Pts[Pts.Count - 1]);
                    break;
            }
            return gp;
        }

        public bool Hit(Point p, float tolerance)
        {
            if (Kind == ItemKind.Redact) return Box.Contains(p);
            using (var gp = Outline())
            using (var pen = new Pen(Color.Black, Width + tolerance * 2))
                return gp.IsOutlineVisible(p, pen);
        }

        public void Draw(Graphics g)
        {
            if (Kind == ItemKind.Redact)
            {
                using (var b = new SolidBrush(Color.Black)) g.FillRectangle(b, Box);
                return;
            }
            var c = Kind == ItemKind.Highlighter ? Color.FromArgb(110, Color) : Color;
            if (IsStroke && Pts.Count == 1)
            {
                using (var b = new SolidBrush(c)) g.FillEllipse(b, Pts[0].X - Width / 2, Pts[0].Y - Width / 2, Width, Width);
                return;
            }
            using (var pen = new Pen(c, Width))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                switch (Kind)
                {
                    case ItemKind.Pen:
                    case ItemKind.Highlighter:
                        g.DrawLines(pen, Pts.ToArray());
                        break;
                    case ItemKind.Line:
                        g.DrawLine(pen, Pts[0], Pts[Pts.Count - 1]);
                        break;
                    case ItemKind.Rect:
                        g.DrawRectangle(pen, Norm());
                        break;
                    case ItemKind.Oval:
                        var r = Norm();
                        if (r.Width > 0 && r.Height > 0) g.DrawEllipse(pen, r);
                        break;
                    case ItemKind.Arrow:
                        DrawArrow(g, pen, c);
                        break;
                }
            }
        }

        void DrawArrow(Graphics g, Pen pen, Color c)
        {
            PointF a = Pts[0], b = Pts[Pts.Count - 1];
            float dx = b.X - a.X, dy = b.Y - a.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            float ux = dx / len, uy = dy / len, L = Math.Min(HeadLength, len), hw = L * 0.55f;
            var baseP = new PointF(b.X - ux * L, b.Y - uy * L);
            pen.EndCap = LineCap.Flat;
            g.DrawLine(pen, a, new PointF(b.X - ux * L * 0.6f, b.Y - uy * L * 0.6f));
            using (var br = new SolidBrush(c))
                g.FillPolygon(br, new[] { b, new PointF(baseP.X - uy * hw, baseP.Y + ux * hw), new PointF(baseP.X + uy * hw, baseP.Y - ux * hw) });
        }
    }

    // "Quick redact": finds e-mail addresses and phone numbers with Windows OCR.
    static class Redactor
    {
        static readonly Regex Email = new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}");
        static readonly Regex Phone = new Regex(@"(?<![\w])\+?\(?\d[\d\s\-\.\(\)]{5,}\d(?![\w])");

        public static async Task<List<Rectangle>> Find(Bitmap img)
        {
            var lines = await Ocr.RecognizeLines(img);
            var boxes = new List<Rectangle>();
            foreach (var l in lines)
            {
                var sb = new StringBuilder();
                var starts = new List<int>();
                foreach (var w in l.Words) { if (sb.Length > 0) sb.Append(' '); starts.Add(sb.Length); sb.Append(w.Text); }
                string t = sb.ToString();
                var hits = new List<Match>();
                foreach (Match m in Email.Matches(t)) hits.Add(m);
                foreach (Match m in Phone.Matches(t))
                {
                    int digits = 0;
                    foreach (char ch in m.Value) if (char.IsDigit(ch)) digits++;
                    if (digits >= 7 && digits <= 15) hits.Add(m);
                }
                foreach (var m in hits)
                    for (int i = 0; i < l.Words.Count; i++)
                    {
                        int a = starts[i], b = a + l.Words[i].Text.Length;
                        if (a < m.Index + m.Length && b > m.Index)
                        {
                            var r = Rectangle.Round(l.Words[i].Rect);
                            r.Inflate(3, 3);
                            boxes.Add(r);
                        }
                    }
            }
            return boxes;
        }
    }

    // Windows share sheet for a desktop window. May not be available while running as admin.
    static class ShareUI
    {
        [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDataTransferManagerInterop
        {
            IntPtr GetForWindow(IntPtr appWindow, [In] ref Guid riid);
            void ShowShareUIForWindow(IntPtr appWindow);
        }

        [DllImport("combase.dll", PreserveSig = false)]
        static extern void RoGetActivationFactory(IntPtr classId, [In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);
        [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void WindowsCreateString(string src, int length, out IntPtr hstring);
        [DllImport("combase.dll", PreserveSig = false)]
        static extern void WindowsDeleteString(IntPtr hstring);

        static IDataTransferManagerInterop Interop()
        {
            const string cls = "Windows.ApplicationModel.DataTransfer.DataTransferManager";
            IntPtr h;
            WindowsCreateString(cls, cls.Length, out h);
            try
            {
                var iid = typeof(IDataTransferManagerInterop).GUID;
                object f;
                RoGetActivationFactory(h, ref iid, out f);
                return (IDataTransferManagerInterop)f;
            }
            finally { WindowsDeleteString(h); }
        }

        public static global::Windows.ApplicationModel.DataTransfer.DataTransferManager ForWindow(IntPtr hwnd)
        {
            var iid = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");   // IDataTransferManager
            IntPtr p = Interop().GetForWindow(hwnd, ref iid);
            try { return (global::Windows.ApplicationModel.DataTransfer.DataTransferManager)Marshal.GetObjectForIUnknown(p); }
            finally { Marshal.Release(p); }
        }

        public static void Show(IntPtr hwnd) { Interop().ShowShareUIForWindow(hwnd); }
    }

    class Canvas : Panel
    {
        public Canvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        }
    }

    class Editor : Form
    {
        enum Tool { Pen, Highlighter, Eraser, Shape, Crop }

        static readonly Color[] PenColors = { Color.FromArgb(240, 50, 50), Color.Black, Color.White, Color.FromArgb(0, 120, 215), Color.FromArgb(16, 170, 80), Color.FromArgb(255, 190, 0), Color.FromArgb(150, 80, 220) };
        static readonly string[] PenNames = { "Red", "Black", "White", "Blue", "Green", "Orange", "Purple" };
        static readonly Color[] HlColors = { Color.Yellow, Color.FromArgb(0, 255, 70), Color.FromArgb(0, 220, 255), Color.FromArgb(255, 80, 200), Color.FromArgb(255, 150, 0) };
        static readonly string[] HlNames = { "Yellow", "Green", "Cyan", "Pink", "Orange" };
        static readonly float[] PenSizes = { 2, 3, 5, 8, 12 };
        static readonly string[] PenSizeNames = { "Extra fine", "Fine", "Medium", "Thick", "Extra thick" };
        static readonly float[] HlSizes = { 10, 16, 24, 34 };
        static readonly string[] HlSizeNames = { "Fine", "Medium", "Thick", "Extra thick" };
        static readonly ItemKind[] Shapes = { ItemKind.Line, ItemKind.Arrow, ItemKind.Rect, ItemKind.Oval };
        static readonly string[] ShapeNames = { "Line", "Arrow", "Rectangle", "Oval" };
        static readonly float[] ZoomSteps = { 0.1f, 0.25f, 0.33f, 0.5f, 0.67f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f, 4f, 6f, 8f };

        class State { public Bitmap Img; public List<Item> Items; }

        Bitmap img;
        List<Item> items = new List<Item>();
        readonly List<State> undo = new List<State>(), redo = new List<State>();
        readonly List<Bitmap> allImages = new List<Bitmap>();
        Tool tool = Tool.Pen, lastDrawTool = Tool.Pen;
        ItemKind shape = ItemKind.Rect;
        Color penColor = PenColors[0], hlColor = HlColors[0];
        int penSize = 1, hlSize = 1;
        float zoom = 1f;
        Item cur;
        bool erasing, erasedAny, cropping, editedSinceSave;
        Point cropStart;
        Rectangle cropRect;
        string sharePath;
        global::Windows.ApplicationModel.DataTransfer.DataTransferManager dtm;

        readonly Panel scroller;
        readonly Canvas canvas;
        readonly ToolStrip ts;
        readonly ToolStripButton bPen, bHl, bEr, bCrop, bUndo, bRedo, bZoom;
        readonly ToolStripDropDownButton bShape, bColor, bSize;
        readonly Action<int> newSnip;
        readonly Settings settings;
        readonly float s;
        readonly int pad;

        public Editor(Bitmap image, Action<int> newSnip, Settings settings, float scale)
        {
            img = image; allImages.Add(img);
            this.newSnip = newSnip; this.settings = settings; s = scale; pad = (int)(16 * s);
            Text = "QuickSnip";
            if (App.AppIcon != null) Icon = App.AppIcon;
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);

            ts = new ToolStrip();
            ts.Dock = DockStyle.Top;
            ts.GripStyle = ToolStripGripStyle.Hidden;
            ts.Renderer = new DarkRenderer();
            ts.BackColor = Theme.Panel; ts.ForeColor = Theme.Text;
            ts.Padding = new Padding((int)(6 * s), (int)(4 * s), (int)(6 * s), (int)(4 * s));
            ts.ImageScalingSize = new Size((int)(14 * s), (int)(14 * s));

            var bNew = new ToolStripSplitButton("New snip");
            bNew.ToolTipText = "New snip (Ctrl+N) - use the arrow for a delayed snip";
            bNew.ButtonClick += delegate { NewSnip(0); };
            App.DarkDrop(bNew);
            foreach (int d in new[] { 3, 5, 10 }) { int dd = d; bNew.DropDownItems.Add("Snip in " + d + " seconds", null, delegate { NewSnip(dd); }); }

            bPen = Btn("Pen", "Pen (P)", delegate { SetTool(Tool.Pen); });
            bHl = Btn("Highlighter", "Highlighter (H)", delegate { SetTool(Tool.Highlighter); });
            bEr = Btn("Eraser", "Eraser - click a mark to remove it (E)", delegate { SetTool(Tool.Eraser); });

            bShape = new ToolStripDropDownButton("Shapes");
            bShape.ToolTipText = "Line, arrow, rectangle, oval (S)";
            App.DarkDrop(bShape);
            for (int i = 0; i < Shapes.Length; i++) { var k = Shapes[i]; bShape.DropDownItems.Add(ShapeNames[i], null, delegate { shape = k; SetTool(Tool.Shape); }); }

            bColor = new ToolStripDropDownButton("Color");
            bColor.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            App.DarkDrop(bColor);
            bColor.DropDownOpening += delegate { FillColorMenu(); };

            bSize = new ToolStripDropDownButton("Size");
            App.DarkDrop(bSize);
            bSize.DropDownOpening += delegate { FillSizeMenu(); };

            bCrop = Btn("Crop", "Crop - drag over the part to keep (C)", delegate { SetTool(Tool.Crop); });
            bUndo = Btn("Undo", "Undo (Ctrl+Z)", delegate { Undo(); });
            bRedo = Btn("Redo", "Redo (Ctrl+Y)", delegate { Redo(); });
            var bOut = Btn(" - ", "Zoom out (Ctrl+Minus)", delegate { ZoomStep(-1); });
            bZoom = Btn("100%", "Actual size (Ctrl+0)", delegate { SetZoom(1f); });
            var bIn = Btn(" + ", "Zoom in (Ctrl+Plus)", delegate { ZoomStep(1); });

            var bText = new ToolStripDropDownButton("Text");
            bText.ToolTipText = "Text actions";
            App.DarkDrop(bText);
            var miCopyText = new ToolStripMenuItem("Copy all text", null, delegate { CopyAllText(); });
            miCopyText.ShortcutKeyDisplayString = "Ctrl+Shift+C";
            bText.DropDownItems.Add(miCopyText);
            bText.DropDownItems.Add("Quick redact (e-mails and phone numbers)", null, delegate { QuickRedact(); });

            var bCopy = Btn("Copy", "Copy (Ctrl+C)", delegate { CopyOut(); });
            var bSave = Btn("Save as", "Save as (Ctrl+S)", delegate { SaveAs(); });

            var bMore = new ToolStripDropDownButton("More");
            App.DarkDrop(bMore);
            var miPrint = new ToolStripMenuItem("Print...", null, delegate { PrintSnip(); });
            miPrint.ShortcutKeyDisplayString = "Ctrl+P";
            bMore.DropDownItems.Add(miPrint);
            bMore.DropDownItems.Add("Open with...", null, delegate { OpenWith(); });
            bMore.DropDownItems.Add("Share", null, delegate { ShareSnip(); });
            bMore.DropDownItems.Add("Open save folder", null, delegate { OpenSaveFolder(); });
            bMore.DropDownItems.Add("Keyboard shortcuts", null, delegate { MessageBox.Show(this, App.ShortcutText, "QuickSnip keyboard shortcuts"); });

            ts.Items.AddRange(new ToolStripItem[] {
                bNew, new ToolStripSeparator(),
                bPen, bHl, bEr, bShape, bColor, bSize, bCrop, new ToolStripSeparator(),
                bUndo, bRedo, new ToolStripSeparator(),
                bOut, bZoom, bIn, new ToolStripSeparator(),
                bText, bCopy, bSave, bMore });

            scroller = new Panel();
            scroller.Dock = DockStyle.Fill;
            scroller.AutoScroll = true;
            scroller.AutoScrollMargin = new Size(pad, pad);
            scroller.BackColor = Color.FromArgb(24, 24, 24);

            canvas = new Canvas();
            canvas.Size = img.Size;
            canvas.Location = new Point(pad, pad);
            canvas.Paint += delegate(object o, PaintEventArgs e) { PaintCanvas(e.Graphics); };
            canvas.MouseDown += CanvasDown;
            canvas.MouseMove += CanvasMove;
            canvas.MouseUp += CanvasUp;
            canvas.MouseWheel += CanvasWheel;
            canvas.MouseEnter += delegate { if (ContainsFocus) canvas.Focus(); };
            scroller.Controls.Add(canvas);
            scroller.Resize += delegate { CenterCanvas(); };

            Controls.Add(ts);
            Controls.Add(scroller);
            scroller.BringToFront();

            var wa = System.Windows.Forms.Screen.FromPoint(Cursor.Position).WorkingArea;
            int w = Math.Min(Math.Max(img.Width + pad * 2 + 24, (int)(820 * s)), (int)(wa.Width * 0.9));
            int h = Math.Min(Math.Max(img.Height + pad * 2 + ts.PreferredSize.Height + 24, (int)(420 * s)), (int)(wa.Height * 0.9));
            ClientSize = new Size(w, h);
            MinimumSize = new Size((int)(480 * s), (int)(320 * s));

            UpdateTools();
            UpdateUndo();
            Shown += delegate
            {
                Theme.DarkTitleBar(Handle);
                // start zoomed out if the snip doesn't fit, like Snipping Tool
                float fit = Math.Min((scroller.ClientSize.Width - pad * 2) / (float)img.Width, (scroller.ClientSize.Height - pad * 2) / (float)img.Height);
                SetZoom(Math.Max(0.1f, Math.Min(1f, fit)));
                Theme.ForceForeground(Handle);
                canvas.Focus();
            };
        }

        static ToolStripButton Btn(string text, string tip, EventHandler click)
        {
            var b = new ToolStripButton(text);
            b.ToolTipText = tip;
            b.DisplayStyle = ToolStripItemDisplayStyle.Text;
            b.Click += click;
            return b;
        }

        Bitmap Swatch(Color c)
        {
            int z = (int)(14 * s);
            var b = new Bitmap(z, z);
            using (var g = Graphics.FromImage(b))
            {
                using (var br = new SolidBrush(c)) g.FillRectangle(br, 0, 0, z, z);
                using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, z - 1, z - 1);
            }
            return b;
        }

        void FillColorMenu()
        {
            bColor.DropDownItems.Clear();
            bool hl = tool == Tool.Highlighter;
            var cols = hl ? HlColors : PenColors;
            var names = hl ? HlNames : PenNames;
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                var it = new ToolStripMenuItem(names[i], Swatch(c));
                it.Checked = c == (hl ? hlColor : penColor);
                it.Click += delegate { if (tool == Tool.Highlighter) hlColor = c; else penColor = c; UpdateTools(); };
                bColor.DropDownItems.Add(it);
            }
        }

        void FillSizeMenu()
        {
            bSize.DropDownItems.Clear();
            bool hl = tool == Tool.Highlighter;
            var names = hl ? HlSizeNames : PenSizeNames;
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(names[i]);
                it.Checked = idx == (hl ? hlSize : penSize);
                it.Click += delegate { if (tool == Tool.Highlighter) hlSize = idx; else penSize = idx; };
                bSize.DropDownItems.Add(it);
            }
        }

        void SetTool(Tool t)
        {
            if (t == Tool.Pen || t == Tool.Highlighter || t == Tool.Shape) lastDrawTool = t;
            tool = t;
            cropping = false;
            UpdateTools();
            canvas.Invalidate();
        }

        void UpdateTools()
        {
            bPen.Checked = tool == Tool.Pen;
            bHl.Checked = tool == Tool.Highlighter;
            bEr.Checked = tool == Tool.Eraser;
            bCrop.Checked = tool == Tool.Crop;
            bShape.Text = tool == Tool.Shape ? ShapeNames[Array.IndexOf(Shapes, shape)] : "Shapes";
            bShape.Tag = tool == Tool.Shape ? "on" : null;
            bool draws = tool == Tool.Pen || tool == Tool.Highlighter || tool == Tool.Shape;
            bColor.Enabled = draws;
            bSize.Enabled = draws;
            var old = bColor.Image;
            bColor.Image = Swatch(tool == Tool.Highlighter ? hlColor : penColor);
            if (old != null) old.Dispose();
            canvas.Cursor = tool == Tool.Eraser ? Cursors.Hand : Cursors.Cross;
            ts.Invalidate();
        }

        void UpdateUndo() { bUndo.Enabled = undo.Count > 0; bRedo.Enabled = redo.Count > 0; }

        // ---- zoom
        void SetZoom(float z)
        {
            zoom = Math.Max(0.1f, Math.Min(8f, z));
            canvas.Size = new Size(Math.Max(1, (int)Math.Round(img.Width * zoom)), Math.Max(1, (int)Math.Round(img.Height * zoom)));
            bZoom.Text = (int)Math.Round(zoom * 100) + "%";
            CenterCanvas();
            canvas.Invalidate();
        }

        void ZoomStep(int dir)
        {
            float next = zoom;
            if (dir > 0) { foreach (var z in ZoomSteps) if (z > zoom + 0.001f) { next = z; break; } }
            else { for (int i = ZoomSteps.Length - 1; i >= 0; i--) if (ZoomSteps[i] < zoom - 0.001f) { next = ZoomSteps[i]; break; } }
            SetZoom(next);
        }

        void CenterCanvas()
        {
            int x = Math.Max(pad, (scroller.ClientSize.Width - canvas.Width) / 2);
            int y = Math.Max(pad, (scroller.ClientSize.Height - canvas.Height) / 2);
            canvas.Location = new Point(x + scroller.AutoScrollPosition.X, y + scroller.AutoScrollPosition.Y);
        }

        void CanvasWheel(object o, MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != 0) { ZoomStep(e.Delta > 0 ? 1 : -1); return; }
            var pos = scroller.AutoScrollPosition;
            if ((ModifierKeys & Keys.Shift) != 0) scroller.AutoScrollPosition = new Point(-pos.X - e.Delta, -pos.Y);
            else scroller.AutoScrollPosition = new Point(-pos.X, -pos.Y - e.Delta);
        }

        Point ToImg(Point p) { return new Point((int)Math.Floor(p.X / zoom), (int)Math.Floor(p.Y / zoom)); }

        void InvalidateImg(Rectangle r)
        {
            canvas.Invalidate(Rectangle.FromLTRB((int)Math.Floor(r.Left * zoom) - 2, (int)Math.Floor(r.Top * zoom) - 2,
                                                 (int)Math.Ceiling(r.Right * zoom) + 2, (int)Math.Ceiling(r.Bottom * zoom) + 2));
        }

        // ---- drawing
        void PaintCanvas(Graphics g)
        {
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.InterpolationMode = zoom < 1f ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
            g.ScaleTransform(zoom, zoom);
            g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var it in items) it.Draw(g);
            if (cropping && cropRect.Width > 0 && cropRect.Height > 0)
            {
                using (var dim = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                using (var rg = new Region(new Rectangle(0, 0, img.Width, img.Height)))
                {
                    rg.Exclude(cropRect);
                    g.FillRegion(dim, rg);
                }
                using (var p = new Pen(Color.White, 1f / zoom)) g.DrawRectangle(p, cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height);
            }
        }

        public Bitmap Compose()
        {
            var b = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var it in items) it.Draw(g);
            }
            return b;
        }

        State Snapshot() { var st = new State(); st.Img = img; st.Items = new List<Item>(items); return st; }
        void PushUndo() { undo.Add(Snapshot()); redo.Clear(); }

        void Restore(State st)
        {
            bool resized = st.Img.Size != img.Size;
            img = st.Img;
            items = st.Items;
            if (resized) SetZoom(zoom);
            canvas.Invalidate();
        }

        void Undo()
        {
            if (undo.Count == 0) return;
            redo.Add(Snapshot());
            var st = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            Restore(st);
            Changed();
        }

        void Redo()
        {
            if (redo.Count == 0) return;
            undo.Add(Snapshot());
            var st = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            Restore(st);
            Changed();
        }

        // like Snipping Tool: every change can go straight to the clipboard
        void Changed()
        {
            editedSinceSave = true;
            UpdateUndo();
            if (settings.AutoCopyEdits) CopyOut();
        }

        bool EraseAt(Point p)
        {
            float tol = 6 * s / zoom;
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i].Hit(p, tol))
                {
                    var r = items[i].Bounds();
                    items.RemoveAt(i);
                    InvalidateImg(r);
                    return true;
                }
            return false;
        }

        void CanvasDown(object o, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            canvas.Focus();
            var p = ToImg(e.Location);
            if (tool == Tool.Crop) { cropping = true; cropStart = p; cropRect = Rectangle.Empty; canvas.Invalidate(); return; }
            PushUndo();
            if (tool == Tool.Eraser) { erasing = true; erasedAny = EraseAt(p); return; }
            cur = new Item();
            if (tool == Tool.Highlighter) { cur.Kind = ItemKind.Highlighter; cur.Color = hlColor; cur.Width = HlSizes[hlSize] * s; }
            else { cur.Kind = tool == Tool.Shape ? shape : ItemKind.Pen; cur.Color = penColor; cur.Width = PenSizes[penSize] * s; }
            cur.Pts.Add(p);
            if (!cur.IsStroke) cur.Pts.Add(p);
            items.Add(cur);
            InvalidateImg(cur.Bounds());
        }

        void CanvasMove(object o, MouseEventArgs e)
        {
            var p = ToImg(e.Location);
            if (cropping)
            {
                cropRect = Rectangle.FromLTRB(Math.Min(cropStart.X, p.X), Math.Min(cropStart.Y, p.Y), Math.Max(cropStart.X, p.X) + 1, Math.Max(cropStart.Y, p.Y) + 1);
                cropRect.Intersect(new Rectangle(0, 0, img.Width, img.Height));
                canvas.Invalidate();
                return;
            }
            if (erasing) { if (EraseAt(p)) erasedAny = true; return; }
            if (cur == null) return;
            var before = cur.Bounds();
            if (cur.IsStroke)
            {
                if (cur.Pts[cur.Pts.Count - 1] == p) return;
                cur.Pts.Add(p);
            }
            else cur.Pts[1] = p;
            InvalidateImg(Rectangle.Union(before, cur.Bounds()));
        }

        void CanvasUp(object o, MouseEventArgs e)
        {
            if (cropping)
            {
                cropping = false;
                if (cropRect.Width > 3 && cropRect.Height > 3) ApplyCrop(cropRect);
                canvas.Invalidate();
                return;
            }
            if (erasing)
            {
                erasing = false;
                if (!erasedAny) { undo.RemoveAt(undo.Count - 1); UpdateUndo(); } else Changed();
                return;
            }
            if (cur == null) return;
            if (!cur.IsStroke && cur.Pts[0] == cur.Pts[1])   // a click without dragging: no shape
            {
                items.Remove(cur);
                undo.RemoveAt(undo.Count - 1);
                InvalidateImg(cur.Bounds());
                cur = null;
                UpdateUndo();
                return;
            }
            cur = null;
            Changed();
        }

        public void ApplyCrop(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, img.Width, img.Height));
            if (r.Width < 2 || r.Height < 2) return;
            PushUndo();
            var ni = Overlay.Crop(img, r);
            allImages.Add(ni);
            var shifted = new List<Item>();
            foreach (var it in items) shifted.Add(it.Shifted(-r.X, -r.Y));
            img = ni;
            items = shifted;
            SetTool(lastDrawTool);
            SetZoom(zoom);
            Changed();
        }

        // ---- output
        void CopyOut() { using (var b = Compose()) Clip.Copy(b); }

        bool SaveAs()
        {
            using (var d = new SaveFileDialog())
            {
                d.Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|GIF image (*.gif)|*.gif";
                d.InitialDirectory = App.SaveDir;
                d.FileName = "Screenshot " + DateTime.Now.ToString("yyyy-MM-dd HHmmss");
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                var fmt = d.FilterIndex == 2 ? ImageFormat.Jpeg : d.FilterIndex == 3 ? ImageFormat.Gif : ImageFormat.Png;
                using (var b = Compose()) b.Save(d.FileName, fmt);
                editedSinceSave = false;
                return true;
            }
        }

        string TempCopy()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "QuickSnip");
                Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, "Snip " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ".png");
                using (var b = Compose()) b.Save(p, ImageFormat.Png);
                return p;
            }
            catch { return null; }
        }

        void PrintSnip()
        {
            using (var bmp = Compose())
            using (var doc = new System.Drawing.Printing.PrintDocument())
            using (var dlg = new PrintDialog())
            {
                doc.DocumentName = "QuickSnip snip";
                dlg.Document = doc;
                dlg.UseEXDialog = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                doc.PrintPage += delegate(object o, System.Drawing.Printing.PrintPageEventArgs e)
                {
                    var m = e.MarginBounds;   // hundredths of an inch
                    float nw = bmp.Width * 100f / (96f * s), nh = bmp.Height * 100f / (96f * s);
                    float f = Math.Min(1f, Math.Min(m.Width / nw, m.Height / nh));
                    e.Graphics.DrawImage(bmp, m.X, m.Y, nw * f, nh * f);
                };
                try { doc.Print(); }
                catch (Exception ex) { MessageBox.Show(this, "Printing failed: " + ex.Message, "QuickSnip"); }
            }
        }

        void OpenWith()
        {
            string p = TempCopy();
            if (p != null) Process.Start("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + p);
        }

        void OpenSaveFolder()
        {
            try { Directory.CreateDirectory(App.SaveDir); } catch { }
            Process.Start("explorer.exe", "\"" + App.SaveDir + "\"");
        }

        void ShareSnip()
        {
            sharePath = TempCopy();
            if (sharePath == null) return;
            try
            {
                if (dtm == null)
                {
                    dtm = ShareUI.ForWindow(Handle);
                    dtm.DataRequested += OnDataRequested;
                }
                ShareUI.Show(Handle);
            }
            catch (Exception)
            {
                // Windows' share sheet can refuse admin apps: fall back to the file in Explorer
                Process.Start("explorer.exe", "/select,\"" + sharePath + "\"");
                TextHint.Popup("Share isn't available here - right-click the file and choose Share", Cursor.Position, s);
            }
        }

        async void OnDataRequested(global::Windows.ApplicationModel.DataTransfer.DataTransferManager sender,
                                   global::Windows.ApplicationModel.DataTransfer.DataRequestedEventArgs args)
        {
            var req = args.Request;
            var deferral = req.GetDeferral();
            try
            {
                var file = await Ocr.Wait(global::Windows.Storage.StorageFile.GetFileFromPathAsync(sharePath));
                req.Data.Properties.Title = "Snip";
                req.Data.SetStorageItems(new global::Windows.Storage.IStorageItem[] { file });
                req.Data.SetBitmap(global::Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            }
            catch (Exception ex) { req.FailWithDisplayText(ex.Message); }
            finally { deferral.Complete(); }
        }

        // Reads the snip itself (pen marks don't get in the way) and copies the text.
        async void CopyAllText()
        {
            var at = Cursor.Position;
            string text;
            try
            {
                Cursor = Cursors.WaitCursor;
                text = await Ocr.Recognize(img);
            }
            catch (Exception ex) { TextHint.Popup("Text extractor failed: " + ex.Message, at, s); return; }
            finally { Cursor = Cursors.Default; }
            if (string.IsNullOrWhiteSpace(text)) { TextHint.Popup("No text found", at, s); return; }
            Clip.CopyText(text);
            TextHint.Popup("Text copied", at, s);
        }

        // Blacks out e-mail addresses and phone numbers (undo removes them again).
        async void QuickRedact()
        {
            var at = Cursor.Position;
            List<Rectangle> boxes;
            try
            {
                Cursor = Cursors.WaitCursor;
                boxes = await Redactor.Find(img);
            }
            catch (Exception ex) { TextHint.Popup("Quick redact failed: " + ex.Message, at, s); return; }
            finally { Cursor = Cursors.Default; }
            if (boxes.Count == 0) { TextHint.Popup("No e-mails or phone numbers found", at, s); return; }
            AddRedactions(boxes);
            TextHint.Popup("Redacted " + boxes.Count + (boxes.Count == 1 ? " item" : " items"), at, s);
        }

        public void AddRedactions(List<Rectangle> boxes)
        {
            PushUndo();
            foreach (var b in boxes)
            {
                var it = new Item();
                it.Kind = ItemKind.Redact;
                it.Color = Color.Black;
                it.Box = b;
                items.Add(it);
            }
            canvas.Invalidate();
            Changed();
        }

        // for --selftest only
        public void AddTestItem(ItemKind kind, Point[] pts, Color color, float width)
        {
            var it = new Item();
            it.Kind = kind; it.Color = color; it.Width = width;
            it.Pts.AddRange(pts);
            items.Add(it);
        }

        void NewSnip(int delaySeconds)
        {
            if (delaySeconds > 0) { WindowState = FormWindowState.Minimized; newSnip(delaySeconds); return; }
            Hide();
            newSnip(0);
            if (!IsDisposed) Show();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.Z: Undo(); return true;
                case Keys.Control | Keys.Y: Redo(); return true;
                case Keys.Control | Keys.C: CopyOut(); return true;
                case Keys.Control | Keys.Shift | Keys.C: CopyAllText(); return true;
                case Keys.Control | Keys.S: SaveAs(); return true;
                case Keys.Control | Keys.P: PrintSnip(); return true;
                case Keys.Control | Keys.N: NewSnip(0); return true;
                case Keys.Control | Keys.Oemplus: case Keys.Control | Keys.Add: ZoomStep(1); return true;
                case Keys.Control | Keys.OemMinus: case Keys.Control | Keys.Subtract: ZoomStep(-1); return true;
                case Keys.Control | Keys.D0: case Keys.Control | Keys.NumPad0: SetZoom(1f); return true;
                case Keys.P: SetTool(Tool.Pen); return true;
                case Keys.H: SetTool(Tool.Highlighter); return true;
                case Keys.E: SetTool(Tool.Eraser); return true;
                case Keys.S: SetTool(Tool.Shape); return true;
                case Keys.C: SetTool(Tool.Crop); return true;
                case Keys.Escape: if (tool == Tool.Crop) { SetTool(lastDrawTool); return true; } break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (settings.AskToSave && editedSinceSave && e.CloseReason == CloseReason.UserClosing)
            {
                var r = MessageBox.Show(this, "Save the changes to this snip?", "QuickSnip", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel || (r == DialogResult.Yes && !SaveAs())) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var b in allImages) b.Dispose();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------ start with Windows (as admin)
    static class Startup
    {
        const string TaskName = "QuickSnip";

        static int Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.CreateNoWindow = true; psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        public static bool Exists() { try { return Run("schtasks.exe", "/Query /TN \"" + TaskName + "\"") == 0; } catch { return false; } }
        public static bool Remove() { return Run("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F") == 0; }
        public static void StartNow() { Run("schtasks.exe", "/Run /TN \"" + TaskName + "\""); }

        // A logon task with "highest privileges" starts QuickSnip as admin without a UAC prompt each time.
        public static bool Install()
        {
            string user, exe = Application.ExecutablePath;
            using (var id = WindowsIdentity.GetCurrent()) user = id.Name;
            string u = System.Security.SecurityElement.Escape(user), x = System.Security.SecurityElement.Escape(exe);
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>QuickSnip: Print Screen snipping tool, started as admin so it also works over admin windows.</Description></RegistrationInfo>\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + u + "</UserId></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + u + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
                "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
                "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <Hidden>false</Hidden>\r\n" +
                "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
                "    <WakeToRun>false</WakeToRun>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>5</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + x + "</Command></Exec></Actions>\r\n" +
                "</Task>\r\n";
            string tmp = Path.Combine(Path.GetTempPath(), "QuickSnip-task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int rc = Run("schtasks.exe", "/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
            try { File.Delete(tmp); } catch { }
            return rc == 0;
        }
    }

    // ------------------------------------------------------------------ tray app
    class App : ApplicationContext
    {
        public static Icon AppIcon;
        public static string SaveDir = Settings.DefaultSaveDir;   // current save folder (also used by "Save as")

        readonly NotifyIcon tray;
        readonly KeyHook hook;
        readonly Control invoker;
        readonly Settings settings;
        readonly float s;
        readonly ToolStripMenuItem miStartup, miMarkup, miAutoSave;
        readonly List<Editor> editors = new List<Editor>();
        bool snipping;
        Bitmap lastSnip;
        System.Windows.Forms.Timer delayTimer;

        public static Icon MakeIcon()
        {
            var b = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var gp = Theme.Rounded(new RectangleF(1, 1, 30, 30), 7))
                using (var br = new SolidBrush(Color.FromArgb(0, 103, 192)))
                    g.FillPath(br, gp);
                using (var p = new Pen(Color.White, 2.4f))
                {
                    p.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(p, 8, 9, 16, 13);
                }
            }
            return Icon.FromHandle(b.GetHicon());
        }

        public static void DarkDrop(ToolStripDropDownItem it)
        {
            it.DropDown.Renderer = new DarkRenderer();
            it.DropDown.BackColor = Theme.Panel;
            it.DropDown.ForeColor = Theme.Text;
        }

        ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set)
        {
            var mi = new ToolStripMenuItem(text);
            mi.Checked = get();
            mi.CheckOnClick = true;
            mi.CheckedChanged += delegate { set(mi.Checked); settings.Save(); };
            return mi;
        }

        void Post(Action a) { invoker.BeginInvoke(a); }

        public App()
        {
            s = Theme.Scale;
            AppIcon = MakeIcon();
            invoker = new Control();
            var force = invoker.Handle;   // creates the window handle used to marshal hook events
            settings = Settings.Load();
            SaveDir = settings.SaveDir;

            var menu = new ContextMenuStrip();
            menu.Renderer = new DarkRenderer();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.Items.Add("New snip   (Print Screen)", null, delegate { Post(delegate { StartSnip(null); }); });
            var delay = new ToolStripMenuItem("Snip with delay");
            DarkDrop(delay);
            foreach (int d in new[] { 3, 5, 10 }) { int dd = d; delay.DropDownItems.Add(d + " seconds", null, delegate { DelaySnip(dd); }); }
            menu.Items.Add(delay);
            miMarkup = new ToolStripMenuItem("Mark up last snip");
            miMarkup.Click += delegate { OpenEditor(); };
            menu.Items.Add(miMarkup);
            menu.Items.Add("Open save folder", null, delegate { OpenSaveFolder(); });
            menu.Items.Add(new ToolStripSeparator());

            var set = new ToolStripMenuItem("Settings");
            DarkDrop(set);
            miAutoSave = Toggle("Auto-save snips", () => settings.AutoSave, v => settings.AutoSave = v);
            set.DropDownItems.Add(miAutoSave);
            set.DropDownItems.Add(Toggle("Add a border to snips", () => settings.Border, v => settings.Border = v));
            set.DropDownItems.Add(Toggle("Win+Shift+S opens QuickSnip", () => settings.WinShiftS, v => { settings.WinShiftS = v; hook.WinShiftS = v; }));
            set.DropDownItems.Add(new ToolStripSeparator());
            set.DropDownItems.Add(Toggle("Open the markup window after each snip", () => settings.OpenEditorAfterSnip, v => settings.OpenEditorAfterSnip = v));
            set.DropDownItems.Add(Toggle("Open each snip in its own window", () => settings.MultipleWindows, v => settings.MultipleWindows = v));
            set.DropDownItems.Add(Toggle("Automatically copy changes", () => settings.AutoCopyEdits, v => settings.AutoCopyEdits = v));
            set.DropDownItems.Add(Toggle("Ask to save edited snips", () => settings.AskToSave, v => settings.AskToSave = v));
            menu.Items.Add(set);

            miStartup = new ToolStripMenuItem("Start with Windows as admin");
            miStartup.Click += delegate { ToggleStartup(); };
            menu.Items.Add(miStartup);
            menu.Items.Add("Keyboard shortcuts", null, delegate { ShowShortcuts(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit QuickSnip", null, delegate { ExitThread(); });
            menu.Opening += delegate { miStartup.Checked = Startup.Exists(); miMarkup.Enabled = lastSnip != null; };

            tray = new NotifyIcon();
            tray.Icon = AppIcon;
            tray.Text = "QuickSnip - press Print Screen to snip";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.MouseClick += delegate(object o, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Post(delegate { StartSnip(null); }); };

            hook = new KeyHook(invoker);
            hook.WinShiftS = settings.WinShiftS;
            hook.Pressed += OnHotKey;

            if (!hook.Ok)
                tray.ShowBalloonTip(8000, "QuickSnip", "Couldn't listen for the Print Screen key. Click the tray icon to snip instead.", ToolTipIcon.Warning);
            else if (!Program.IsAdmin)
                tray.ShowBalloonTip(8000, "QuickSnip is running (not as admin)",
                    "Print Screen works, except while an admin window is focused. Right-click the tray icon > Start with Windows as admin to fix that.", ToolTipIcon.Info);
        }

        void OnHotKey(HotKey k)
        {
            switch (k)
            {
                case HotKey.SnipBar: StartSnip(null); break;
                case HotKey.TextExtract: StartSnip(SnipMode.Text); break;
                case HotKey.FullScreenNow: SnipFullScreen(); break;
                case HotKey.WindowNow: SnipActiveWindow(); break;
            }
        }

        public const string ShortcutText =
            "Anywhere\r\n" +
            "   Print Screen  -  snip bar\r\n" +
            "   Win+Shift+S  -  snip bar (can be turned off in Settings)\r\n" +
            "   Ctrl+Print Screen  -  text extractor\r\n" +
            "   Shift+Print Screen  -  instant full-screen snip (monitor under the mouse)\r\n" +
            "   Ctrl+Shift+Print Screen  -  instant snip of the active window\r\n" +
            "   Alt+Print Screen, Win+Print Screen  -  Windows' own (unchanged)\r\n\r\n" +
            "Snip bar\r\n" +
            "   Esc or right-click  -  cancel\r\n" +
            "   M  -  magnifier on/off\r\n\r\n" +
            "Markup window\r\n" +
            "   P pen   H highlighter   E eraser   S shapes   C crop\r\n" +
            "   Ctrl+Z undo   Ctrl+Y redo\r\n" +
            "   Ctrl+C copy   Ctrl+Shift+C copy all text   Ctrl+S save as   Ctrl+P print\r\n" +
            "   Ctrl+N new snip\r\n" +
            "   Ctrl+Plus / Ctrl+Minus / Ctrl+mouse wheel  zoom   Ctrl+0 actual size";

        void ShowShortcuts()
        {
            MessageBox.Show(ShortcutText, "QuickSnip keyboard shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void OpenSaveFolder()
        {
            try { Directory.CreateDirectory(settings.SaveDir); } catch { }
            Process.Start("explorer.exe", "\"" + settings.SaveDir + "\"");
        }

        void ToggleStartup()
        {
            bool exists = Startup.Exists();
            if (Program.IsAdmin)
            {
                bool ok = exists ? Startup.Remove() : Startup.Install();
                tray.ShowBalloonTip(4000, "QuickSnip", ok ? (exists ? "Won't start with Windows anymore." : "Will start as admin when you sign in.") : "That didn't work.", ok ? ToolTipIcon.Info : ToolTipIcon.Error);
                return;
            }
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, exists ? "--remove-startup" : "--install-startup");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                var p = Process.Start(psi);
                if (exists)
                {
                    p.WaitForExit();
                    tray.ShowBalloonTip(4000, "QuickSnip", "Won't start with Windows anymore.", ToolTipIcon.Info);
                }
                else ExitThread();   // the admin helper restarts QuickSnip as admin once this copy has closed
            }
            catch (Win32Exception) { }   // UAC prompt was cancelled
        }

        void DelaySnip(int seconds)
        {
            if (delayTimer != null) delayTimer.Dispose();
            TextHint.Popup("Snipping in " + seconds + " seconds", Cursor.Position, s);   // gone before the capture
            delayTimer = new System.Windows.Forms.Timer();
            delayTimer.Interval = seconds * 1000;
            delayTimer.Tick += delegate { delayTimer.Stop(); StartSnip(null); };
            delayTimer.Start();
        }

        // Markup window's "New snip": give it a moment to disappear before the screen is captured.
        void SnipFromEditor(int delaySeconds)
        {
            if (delaySeconds > 0) { DelaySnip(delaySeconds); return; }
            Application.DoEvents();
            Thread.Sleep(200);
            StartSnip(null);
        }

        void StartSnip(SnipMode? force)
        {
            if (snipping) return;
            snipping = true;
            Bitmap result = null;
            bool textMode = false;
            Color? picked = null;
            try
            {
                var vs = SystemInformation.VirtualScreen;
                var mons = new List<Rectangle>();
                foreach (var sc in System.Windows.Forms.Screen.AllScreens) { var r = sc.Bounds; r.Offset(-vs.X, -vs.Y); mons.Add(r); }
                var prim = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                prim.Offset(-vs.X, -vs.Y);
                var wins = Capture.Windows(vs);
                using (var shot = Capture.Screen(vs))
                using (var ov = new Overlay(shot, vs, force ?? settings.Mode, wins, mons, prim, settings.SaveDir, settings.AutoSave, s))
                {
                    ov.MagnifierOn = settings.Magnifier;
                    ov.ShowDialog();
                    if (ov.MagnifierOn != settings.Magnifier) { settings.Magnifier = ov.MagnifierOn; settings.Save(); }
                    textMode = ov.Mode == SnipMode.Text;
                    picked = ov.PickedColor;
                    // text extractor and color picker aren't remembered as the default mode
                    var keepMode = (ov.Mode == SnipMode.Text || ov.Mode == SnipMode.ColorPicker) ? settings.Mode : ov.Mode;
                    bool changed = keepMode != settings.Mode || ov.SaveDir != settings.SaveDir || ov.AutoSave != settings.AutoSave;
                    settings.Mode = keepMode;
                    settings.SaveDir = ov.SaveDir;
                    SaveDir = ov.SaveDir;
                    if (ov.AutoSave != settings.AutoSave) miAutoSave.Checked = ov.AutoSave;   // picking a folder turns auto-save on
                    settings.AutoSave = ov.AutoSave;
                    if (changed) settings.Save();
                    result = ov.Result;
                }
            }
            catch (Exception ex)
            {
                tray.ShowBalloonTip(5000, "QuickSnip", "Snip failed: " + ex.Message, ToolTipIcon.Error);
            }
            finally { snipping = false; }

            if (picked.HasValue)
            {
                string hex = Overlay.Hex(picked.Value);
                Clip.CopyText(hex);
                TextHint.Popup("Copied " + hex, Cursor.Position, s);
                return;
            }
            if (result == null) return;
            if (textMode) ExtractText(result); else Deliver(result);
        }

        void SnipFullScreen()
        {
            if (snipping) return;
            Bitmap img;
            try { img = Capture.Screen(System.Windows.Forms.Screen.FromPoint(Cursor.Position).Bounds); }
            catch (Exception ex) { tray.ShowBalloonTip(5000, "QuickSnip", "Snip failed: " + ex.Message, ToolTipIcon.Error); return; }
            Deliver(img);
        }

        void SnipActiveWindow()
        {
            if (snipping) return;
            IntPtr h = Native.GetForegroundWindow();
            Native.RECT r;
            if (h == IntPtr.Zero) { SnipFullScreen(); return; }
            if (Native.DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(Native.RECT))) != 0) Native.GetWindowRect(h, out r);
            var rc = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            rc.Intersect(SystemInformation.VirtualScreen);
            if (rc.Width < 2 || rc.Height < 2) { SnipFullScreen(); return; }
            Bitmap img;
            try { img = Capture.Screen(rc); }
            catch (Exception ex) { tray.ShowBalloonTip(5000, "QuickSnip", "Snip failed: " + ex.Message, ToolTipIcon.Error); return; }
            Deliver(img);
        }

        // Text extractor: the text goes to the clipboard; the picture isn't saved.
        async void ExtractText(Bitmap img)
        {
            var at = Cursor.Position;
            string text;
            try { text = await Ocr.Recognize(img); }
            catch (Exception ex) { TextHint.Popup("Text extractor failed: " + ex.Message, at, s); return; }
            finally { img.Dispose(); }
            if (string.IsNullOrWhiteSpace(text)) { TextHint.Popup("No text found", at, s); return; }
            Clip.CopyText(text);
            TextHint.Popup("Text copied", at, s);
        }

        public static void AddBorder(Bitmap img)
        {
            using (var g = Graphics.FromImage(img))
            using (var p = new Pen(Color.FromArgb(128, 128, 128)))
                g.DrawRectangle(p, 0, 0, img.Width - 1, img.Height - 1);
        }

        // Silent, like Snipping Tool with notifications off: clipboard + save, no popup.
        void Deliver(Bitmap img)
        {
            if (settings.Border) AddBorder(img);
            try { Clip.Copy(img); } catch { }
            if (settings.AutoSave && !TrySave(img, settings.SaveDir) && settings.SaveDir != Settings.DefaultSaveDir)
            {
                if (TrySave(img, Settings.DefaultSaveDir))   // chosen folder unavailable (e.g. drive unplugged)
                    tray.ShowBalloonTip(5000, "QuickSnip", "Couldn't save to " + settings.SaveDir + " - saved to Pictures\\Screenshots instead.", ToolTipIcon.Warning);
            }
            if (lastSnip != null) lastSnip.Dispose();
            lastSnip = img;
            if (settings.OpenEditorAfterSnip) OpenEditor();
        }

        static bool TrySave(Bitmap img, string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string name = "Screenshot " + DateTime.Now.ToString("yyyy-MM-dd HHmmss");
                string path = Path.Combine(dir, name + ".png");
                for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, name + " (" + i + ").png");
                img.Save(path, ImageFormat.Png);
                return true;
            }
            catch { return false; }
        }

        void OpenEditor()
        {
            if (lastSnip == null) return;
            if (!settings.MultipleWindows)
                foreach (var old in editors.ToArray()) old.Close();   // may ask to save first
            var ed = new Editor((Bitmap)lastSnip.Clone(), SnipFromEditor, settings, s);
            ed.FormClosed += delegate { editors.Remove(ed); };
            editors.Add(ed);
            ed.Show();
        }

        protected override void ExitThreadCore()
        {
            hook.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    // ------------------------------------------------------------------ offscreen self-test (no screen capture, no clipboard)
    static class SelfTest
    {
        public static void Run(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var log = new StringBuilder();
            float s = 1f;
            App.AppIcon = App.MakeIcon();
            var vs = new Rectangle(0, 0, 1600, 900);
            var shot = new Bitmap(1600, 900, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(shot))
            {
                using (var lg = new LinearGradientBrush(vs, Color.FromArgb(40, 90, 160), Color.FromArgb(150, 60, 120), 30f)) g.FillRectangle(lg, vs);
                g.FillRectangle(Brushes.WhiteSmoke, 200, 150, 700, 450);
                g.FillRectangle(Brushes.SteelBlue, 200, 150, 700, 32);
                using (var f = new Font("Segoe UI", 20)) g.DrawString("Fake window A", f, Brushes.Black, 230, 220);
                g.FillRectangle(Brushes.Beige, 950, 300, 500, 400);
                g.FillRectangle(Brushes.DarkOliveGreen, 950, 300, 500, 32);
                using (var f = new Font("Segoe UI", 20)) g.DrawString("Fake window B", f, Brushes.Black, 980, 360);
            }
            var wins = new List<Rectangle> { new Rectangle(200, 150, 700, 450), new Rectangle(950, 300, 500, 400) };
            var mons = new List<Rectangle> { vs };
            var free = new List<Point>();
            for (int i = 0; i < 60; i++) { double a = i * Math.PI * 2 / 60; free.Add(new Point(1150 + (int)(220 * Math.Cos(a)), 450 + (int)(150 * Math.Sin(a) * (1 + 0.2 * Math.Cos(3 * a))))); }

            // ---- snip bar / overlay
            using (var ov = new Overlay(shot, vs, SnipMode.Rectangle, wins, mons, vs, @"E:\Clips\Game screenshots", true, s))
            {
                RenderOverlay(ov, SnipMode.Rectangle, new Rectangle(300, 260, 520, 280), Rectangle.Empty, 0, null, Path.Combine(outDir, "overlay-rectangle.png"));
                RenderOverlay(ov, SnipMode.Window, Rectangle.Empty, wins[1], 1, null, Path.Combine(outDir, "overlay-window.png"));
                RenderOverlay(ov, SnipMode.Freeform, Rectangle.Empty, Rectangle.Empty, 3, free.ToArray(), Path.Combine(outDir, "overlay-freeform.png"));
                RenderOverlay(ov, SnipMode.Text, new Rectangle(220, 200, 330, 60), Rectangle.Empty, 4, null, Path.Combine(outDir, "overlay-text.png"));
                RenderOverlay(ov, SnipMode.Rectangle, Rectangle.Empty, Rectangle.Empty, 6, null, Path.Combine(outDir, "overlay-folder-hover.png"));
                ov.SetTestCursor(new Point(560, 166));   // on window A's blue title bar
                RenderOverlay(ov, SnipMode.ColorPicker, Rectangle.Empty, Rectangle.Empty, 5, null, Path.Combine(outDir, "overlay-colorpicker.png"));
                ov.SetTestCursor(new Point(-1, -1));

                // magnifier: idle over window A's title-bar corner, and while dragging a selection
                ov.MagnifierOn = true;
                var at = new Point(200, 150);
                ov.SetTestState(SnipMode.Rectangle, Rectangle.Empty, Rectangle.Empty, 6, null);
                ov.SetTestCursor(at);
                using (var b = new Bitmap(1600, 900))
                {
                    using (var g = Graphics.FromImage(b)) ov.Render(g, vs);
                    b.Save(Path.Combine(outDir, "overlay-magnifier.png"), ImageFormat.Png);
                    // the lens is centred on the cursor pixel: the middle of the centre cell must show that pixel, undimmed
                    int n = 17, cell = ov.TestMagCell;
                    var lens = ov.TestMagCircle(at);
                    var mid = b.GetPixel(lens.X + (n / 2) * cell + cell / 2, lens.Y + (n / 2) * cell + cell / 2);
                    var want = shot.GetPixel(at.X, at.Y);
                    log.AppendLine("magnifier centre pixel " + Overlay.Hex(mid) + " = screen pixel under cursor " + Overlay.Hex(want) + ": " + (mid.ToArgb() == Color.FromArgb(255, want.R, want.G, want.B).ToArgb()));
                }
                ov.SetTestState(SnipMode.Rectangle, new Rectangle(300, 260, 520, 280), Rectangle.Empty, -1, null);
                ov.SetTestDragging(true);
                ov.SetTestCursor(new Point(819, 539));
                using (var b = new Bitmap(1600, 900))
                {
                    using (var g = Graphics.FromImage(b)) ov.Render(g, vs);
                    b.Save(Path.Combine(outDir, "overlay-magnifier-dragging.png"), ImageFormat.Png);
                }
                ov.SetTestDragging(false);

                // crosshair exactly centred, at 100% and at 125% display scaling
                foreach (float sc in new[] { 1f, 1.25f, 1.5f })
                    using (var ovs = new Overlay(shot, vs, SnipMode.Rectangle, wins, mons, vs, Settings.DefaultSaveDir, true, sc))
                    using (var b = new Bitmap(1600, 900))
                    {
                        var p = new Point(700, 400);   // inside window A (light background, so black arms stand out)
                        ovs.MagnifierOn = true;
                        ovs.SetTestCursor(p);
                        using (var g = Graphics.FromImage(b)) ovs.Render(g, vs);
                        var c = ovs.TestMagCircle(p);
                        int cl = ovs.TestMagCell, mx = c.X + 8 * cl + cl / 2, my = c.Y + 8 * cl + cl / 2;
                        Func<int, int, bool> black = (x, y) => { var px = b.GetPixel(x, y); return px.R < 20 && px.G < 20 && px.B < 20; };
                        // vertical arm: black columns across a row near the top must be centred on mx
                        int yRow = c.Y + cl * 3, minX = int.MaxValue, maxX = int.MinValue;
                        for (int x = c.Left; x < c.Right; x++) if (black(x, yRow)) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
                        // horizontal arm: black rows across a column near the left must be centred on my
                        int xCol = c.X + cl * 3, minY = int.MaxValue, maxY = int.MinValue;
                        for (int y = c.Top; y < c.Bottom; y++) if (black(xCol, y)) { minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
                        // gaps: last black pixel left of centre and first black pixel right of centre, along row my
                        int leftEnd = -1, rightStart = -1;
                        for (int x = mx; x >= c.Left; x--) if (black(x, my)) { leftEnd = x; break; }
                        for (int x = mx; x < c.Right; x++) if (black(x, my)) { rightStart = x; break; }
                        bool centred = minX + maxX == 2 * mx && minY + maxY == 2 * my && (mx - leftEnd) == (rightStart - mx);
                        // arms pure black with exactly 1 px of pure white on both sides, then the magnified screen
                        bool outline = b.GetPixel(minX - 1, yRow).ToArgb() == Color.White.ToArgb() && b.GetPixel(maxX + 1, yRow).ToArgb() == Color.White.ToArgb()
                                    && b.GetPixel(minX - 2, yRow).ToArgb() == b.GetPixel(minX - 4, yRow).ToArgb()
                                    && b.GetPixel(maxX + 2, yRow).ToArgb() == b.GetPixel(maxX + 4, yRow).ToArgb();
                        log.AppendLine("magnifier crosshair at " + (int)(sc * 100) + "% scaling: arm pixel " + Overlay.Hex(b.GetPixel(mx, yRow))
                                     + ", 1 px pure white outline on both sides = " + outline);
                        log.AppendLine("magnifier crosshair at " + (int)(sc * 100) + "% scaling: arms " + (maxX - minX + 1) + " px, centred exactly = " + centred
                                     + " (gap left " + (mx - leftEnd) + ", right " + (rightStart - mx) + ")");
                        if (sc == 1.25f) b.Clone(Rectangle.Inflate(c, 4, 4), b.PixelFormat).Save(Path.Combine(outDir, "magnifier-125.png"), ImageFormat.Png);
                    }

                ov.SetTestState(SnipMode.Rectangle, Rectangle.Empty, Rectangle.Empty, -1, null);
                log.AppendLine("opening with the magnifier on: first paint differs from a full draw by "
                             + ov.TestFirstPaintDiff(new Point(742, 564)) + " pixels (expected 0)");
                ov.MagnifierOn = false;
                ov.SetTestCursor(new Point(-1, -1));

                // while dragging, the bar must not be drawn: compare the bar area with and without a drag in progress
                ov.SetTestState(SnipMode.Rectangle, new Rectangle(300, 260, 520, 280), Rectangle.Empty, -1, null);
                using (var idle = new Bitmap(1600, 900))
                using (var drag = new Bitmap(1600, 900))
                using (var clean = new Bitmap(1600, 900))
                {
                    using (var g = Graphics.FromImage(idle)) ov.Render(g, vs);
                    ov.SetTestDragging(true);
                    using (var g = Graphics.FromImage(drag)) ov.Render(g, vs);
                    ov.SetTestDragging(false);
                    using (var g = Graphics.FromImage(clean))   // what the bar area looks like with nothing on it: dimmed screenshot
                    {
                        g.DrawImage(shot, vs);
                        using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(b, vs);
                    }
                    drag.Save(Path.Combine(outDir, "overlay-dragging.png"), ImageFormat.Png);
                    // close-up of the selection's bottom-right corner, 6x
                    using (var zoomed = new Bitmap(60 * 6, 40 * 6))
                    {
                        using (var g = Graphics.FromImage(zoomed))
                        {
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(drag, new Rectangle(0, 0, zoomed.Width, zoomed.Height), new Rectangle(820 - 50, 540 - 30, 60, 40), GraphicsUnit.Pixel);
                        }
                        zoomed.Save(Path.Combine(outDir, "selection-corner-zoomed.png"), ImageFormat.Png);
                    }
                    // dashes must stay put while the box grows: compare the top edge for two widths
                    using (var w1 = new Bitmap(1600, 900))
                    using (var w2 = new Bitmap(1600, 900))
                    {
                        ov.SetTestState(SnipMode.Rectangle, new Rectangle(300, 260, 520, 280), Rectangle.Empty, -1, null);
                        using (var g = Graphics.FromImage(w1)) ov.Render(g, vs);
                        ov.SetTestState(SnipMode.Rectangle, new Rectangle(300, 260, 547, 293), Rectangle.Empty, -1, null);
                        using (var g = Graphics.FromImage(w2)) ov.Render(g, vs);
                        log.AppendLine("dashes stay still while resizing: top-edge pixels that moved = "
                                     + CountDiffIn(w1, w2, new Rectangle(300, 260, 510, 1)) + ", left-edge = "
                                     + CountDiffIn(w1, w2, new Rectangle(300, 260, 1, 270)) + " (expected 0 and 0)");
                    }
                    var barArea = new Rectangle(560, 10, 480, 70);
                    log.AppendLine("bar hidden while dragging: bar visible when idle = " + (CountDiffIn(idle, clean, barArea) > 0)
                                 + ", bar area while dragging differs from plain screen by " + CountDiffIn(drag, clean, barArea) + " pixels (expected 0)");
                }

                var steps = new List<Rectangle>();
                for (int i = 1; i <= 40; i++) steps.Add(new Rectangle(400, 300, 10 * i + 1, 6 * i + 1));
                for (int i = 40; i >= 5; i -= 3) steps.Add(new Rectangle(400, 300, 10 * i + 1, 6 * i + 1));
                for (int i = 1; i <= 20; i++) steps.Add(Rectangle.FromLTRB(400 - 15 * i, 300 - 9 * i, 401, 301));
                steps.Add(new Rectangle(1000, 600, 300, 200));
                steps.Add(new Rectangle(1100, 650, 50, 40));
                log.AppendLine("incremental repaint: pixels different from a full repaint = " + ov.TestIncrementalDiff(steps.ToArray()));
            }
            log.AppendLine("color picker hex of window A title bar: " + Overlay.Hex(Color.SteelBlue) + " expected, overlay reads " + Overlay.Hex(shot.GetPixel(560, 166)));

            // ---- crosshair
            foreach (int n in new[] { 32, 48, 64, Native.GetSystemMetrics(13) })
                using (var cb = Crosshair.Draw(n))
                {
                    int c = n / 2;
                    bool ok = cb.GetPixel(c, c).ToArgb() == Color.White.ToArgb()
                           && cb.GetPixel(c, 4).ToArgb() == Color.White.ToArgb() && cb.GetPixel(c + 1, 4).A != 255
                           && cb.GetPixel(4, c).ToArgb() == Color.White.ToArgb() && cb.GetPixel(4, c + 1).A != 255;
                    log.AppendLine("crosshair " + n + "px: centre lines on hotspot = " + ok);
                }

            // ---- OCR, quick redact, markup
            using (var tb = new Bitmap(640, 170))
            {
                using (var g = Graphics.FromImage(tb))
                {
                    g.Clear(Color.FromArgb(32, 32, 32));
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    using (var f = new Font("Segoe UI", 14)) g.DrawString("QuickSnip text extractor test", f, Brushes.White, 10, 10);
                    using (var f = new Font("Segoe UI", 11)) g.DrawString("Contact: hello@example.com or +1 555-123-4567", f, Brushes.LightGray, 10, 62);
                    using (var f = new Font("Segoe UI", 9)) g.DrawString("Order #48213 - small settings label", f, Brushes.Silver, 10, 120);
                }
                tb.Save(Path.Combine(outDir, "ocr-input.png"), ImageFormat.Png);

                // background thread (blocking this STA thread would stop the result arriving)
                string ocr;
                try { ocr = Task.Run(() => Ocr.Recognize(tb)).Result; } catch (Exception ex) { ocr = "OCR FAILED: " + ex.Message; }
                log.AppendLine("OCR (background thread):\r\n    " + ocr.Replace("\r\n", "\r\n    "));

                // the way the app calls it: awaited on the UI thread inside a message loop (no window shown)
                string uiOcr = "TIMED OUT";
                var ctx = new ApplicationContext();
                var kick = new System.Windows.Forms.Timer();
                kick.Interval = 50;
                kick.Tick += async (o, e) =>
                {
                    kick.Stop();
                    try { uiOcr = await Ocr.Recognize(tb); }
                    catch (Exception ex) { uiOcr = "FAILED: " + ex.Message; }
                    ctx.ExitThread();
                };
                var guard = new System.Windows.Forms.Timer();
                guard.Interval = 20000;
                guard.Tick += delegate { ctx.ExitThread(); };
                kick.Start(); guard.Start();
                Application.Run(ctx);
                kick.Dispose(); guard.Dispose();
                log.AppendLine("OCR (UI thread, like the app) matches: " + (uiOcr == ocr));

                List<Rectangle> boxes;
                try { boxes = Task.Run(() => Redactor.Find(tb)).Result; } catch (Exception ex) { boxes = new List<Rectangle>(); log.AppendLine("redact FAILED: " + ex.Message); }
                log.AppendLine("quick redact boxes: " + boxes.Count);

                var quiet = new Settings();
                quiet.AutoCopyEdits = false;   // keep the real clipboard untouched
                using (var ed = new Editor((Bitmap)tb.Clone(), delegate(int d) { }, quiet, s))
                {
                    ed.AddTestItem(ItemKind.Pen, new[] { new Point(20, 110), new Point(90, 95), new Point(160, 115), new Point(230, 100) }, Color.FromArgb(240, 50, 50), 3);
                    ed.AddTestItem(ItemKind.Highlighter, new[] { new Point(12, 24), new Point(330, 24) }, Color.Yellow, 16);
                    ed.AddTestItem(ItemKind.Arrow, new[] { new Point(560, 150), new Point(470, 60) }, Color.FromArgb(0, 120, 215), 5);
                    ed.AddTestItem(ItemKind.Rect, new[] { new Point(380, 110), new Point(620, 160) }, Color.FromArgb(16, 170, 80), 3);
                    ed.AddTestItem(ItemKind.Oval, new[] { new Point(340, 4), new Point(420, 50) }, Color.FromArgb(255, 190, 0), 3);
                    ed.AddTestItem(ItemKind.Line, new[] { new Point(10, 160), new Point(300, 160) }, Color.White, 2);
                    ed.AddRedactions(boxes);
                    using (var b = ed.Compose()) b.Save(Path.Combine(outDir, "markup.png"), ImageFormat.Png);
                    ed.ApplyCrop(new Rectangle(0, 50, 640, 120));
                    using (var b = ed.Compose())
                    {
                        b.Save(Path.Combine(outDir, "markup-cropped.png"), ImageFormat.Png);
                        log.AppendLine("crop: 640x170 -> " + b.Width + "x" + b.Height + " (expected 640x120)");
                    }
                }
            }

            // ---- Windows share sheet reachable (not shown)
            using (var f = new Form())
            {
                try
                {
                    var d = ShareUI.ForWindow(f.Handle);
                    log.AppendLine("share: Windows share manager reachable = " + (d != null));
                }
                catch (Exception ex) { log.AppendLine("share: " + ex.GetType().Name + " - " + ex.Message); }
            }

            shot.Dispose();
            File.WriteAllText(Path.Combine(outDir, "results.txt"), log.ToString());
        }

        static int CountDiffIn(Bitmap a, Bitmap b, Rectangle area)
        {
            int n = 0;
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                    if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) n++;
            return n;
        }

        static void RenderOverlay(Overlay ov, SnipMode m, Rectangle sel, Rectangle hover, int hoverBtn, Point[] free, string file)
        {
            ov.SetTestState(m, sel, hover, hoverBtn, free);
            using (var b = new Bitmap(1600, 900))
            {
                using (var g = Graphics.FromImage(b)) ov.Render(g, new Rectangle(0, 0, 1600, 900));
                b.Save(file, ImageFormat.Png);
            }
        }
    }

    // ------------------------------------------------------------------ entry point
    static class Program
    {
        public const string MutexName = "Local\\QuickSnip_SingleInstance";

        public static bool IsAdmin
        {
            get { using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
        }

        [STAThread]
        static void Main(string[] args)
        {
            // per-monitor DPI aware: everything below works in real screen pixels
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { Native.SetProcessDPIAware(); } catch { } }

            string a0 = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (a0 == "--install-startup")
            {
                if (Startup.Install()) { WaitForInstanceExit(); Startup.StartNow(); }
                else MessageBox.Show("QuickSnip couldn't create its startup task.", "QuickSnip");
                return;
            }
            if (a0 == "--remove-startup") { Startup.Remove(); return; }
            if (a0 == "--selftest") { SelfTest.Run(args.Length > 1 ? args[1] : "."); return; }

            Mutex mutex;
            bool created;
            try { mutex = new Mutex(true, MutexName, out created); }
            catch (UnauthorizedAccessException) { return; }   // an admin copy is already running
            using (mutex)
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App());
            }
        }

        static void WaitForInstanceExit()
        {
            for (int i = 0; i < 75; i++)
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(MutexName, out m)) return;
                m.Dispose();
                Thread.Sleep(200);
            }
        }
    }
}
