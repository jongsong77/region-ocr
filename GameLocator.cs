using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace RegionOcr;

/// <summary>
/// 게임 창을 제목으로 찾고, 창 안에서 실제 게임 화면(검은 여백 제외) 위치/크기를 계산.
/// 기준 시점의 게임 화면 대비 배율·위치 변화만큼 영역을 변환 → 모니터/해상도/창 크기가 달라도 동작.
/// </summary>
public static class GameLocator
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);

    public static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>화면 좌표에 있는 최상위 창</summary>
    public static IntPtr RootWindowAt(Point p) => GetAncestor(WindowFromPoint(new POINT { X = p.X, Y = p.Y }), 2 /* GA_ROOT */);

    /// <summary>제목이 같은 창(없으면 제목을 포함하는 창). 최소화/숨김 창 제외</summary>
    public static IntPtr FindWindow(string title)
    {
        IntPtr exact = IntPtr.Zero, partial = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            string t = TitleOf(h);
            if (t == title) { exact = h; return false; }
            if (partial == IntPtr.Zero && t.Length > 0 && t.Contains(title)) partial = h;
            return true;
        }, IntPtr.Zero);
        return exact != IntPtr.Zero ? exact : partial;
    }

    /// <summary>창의 클라이언트 영역(화면 좌표)</summary>
    public static Rectangle ClientRectOnScreen(IntPtr h)
    {
        GetClientRect(h, out var r);
        var p = new POINT();
        ClientToScreen(h, ref p);
        return new Rectangle(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>
    /// 게임 화면 영역(화면 좌표). 클라이언트 영역에서 가로/세로 여러 줄을 훑어 검은 여백(레터박스)을 제외.
    /// </summary>
    public static Rectangle? DetectCanvas(IntPtr h)
    {
        var client = ClientRectOnScreen(h);
        if (client.Width < 50 || client.Height < 50) return null;
        using var bmp = OcrService.Capture(client);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int w = bmp.Width, hh = bmp.Height, stride = data.Stride;
            var px = new byte[stride * hh];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bool Content(int x, int y)
            {
                int o = y * stride + x * 4;
                return Math.Max(px[o], Math.Max(px[o + 1], px[o + 2])) > 20;
            }

            int left = w, right = -1;
            foreach (double fy in new[] { 0.3, 0.5, 0.7 })
            {
                int y = (int)(hh * fy);
                for (int x = 0; x < w; x++) if (Content(x, y)) { left = Math.Min(left, x); break; }
                for (int x = w - 1; x >= 0; x--) if (Content(x, y)) { right = Math.Max(right, x); break; }
            }
            if (right <= left) return null;

            int top = hh, bottom = -1;
            foreach (double fx in new[] { 0.3, 0.5, 0.7 })
            {
                int x = left + (int)((right - left) * fx);
                for (int y = 0; y < hh; y++) if (Content(x, y)) { top = Math.Min(top, y); break; }
                for (int y = hh - 1; y >= 0; y--) if (Content(x, y)) { bottom = Math.Max(bottom, y); break; }
            }
            if (bottom <= top) return null;
            return new Rectangle(client.X + left, client.Y + top, right - left + 1, bottom - top + 1);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>
    /// 클라이언트 영역 안에 가로세로 비율(aspect)을 유지해 가운데 맞춘 게임 화면 계산.
    /// 화면 픽셀을 보지 않으므로 다른 창/오버레이가 겹쳐도 결과가 흔들리지 않음.
    /// </summary>
    public static Rectangle FitCanvas(Rectangle client, double aspect)
    {
        if (client.Width <= 0 || client.Height <= 0 || aspect <= 0) return client;
        if ((double)client.Width / client.Height >= aspect)
        {
            int w = (int)Math.Round(client.Height * aspect);
            return new Rectangle(client.X + (client.Width - w) / 2, client.Y, w, client.Height);
        }
        int h = (int)Math.Round(client.Width / aspect);
        return new Rectangle(client.X, client.Y + (client.Height - h) / 2, client.Width, h);
    }

    /// <summary>기준 게임 화면(from) 기준 사각형을 새 게임 화면(to)에 맞게 변환</summary>
    public static Rectangle Map(Rectangle r, Rectangle from, Rectangle to)
    {
        double sx = (double)to.Width / from.Width, sy = (double)to.Height / from.Height;
        int x = to.X + (int)Math.Round((r.X - from.X) * sx);
        int y = to.Y + (int)Math.Round((r.Y - from.Y) * sy);
        int x2 = to.X + (int)Math.Round((r.Right - from.X) * sx);
        int y2 = to.Y + (int)Math.Round((r.Bottom - from.Y) * sy);
        return Rectangle.FromLTRB(x, y, x2, y2);
    }
}
