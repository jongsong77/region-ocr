using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RegionOcr;

/// <summary>
/// 항상 위 반투명 정보창. 두 가지 모드:
///  - 텍스트: 줄마다 색을 지정해 직접 그림
///  - 그래프: 몇 줄의 값 + 최근 구간 그래프
/// 포커스를 가져가지 않으며(게임 입력 방해 X) 왼쪽 드래그로 이동, 오른쪽 클릭으로 모드 전환.
/// </summary>
public sealed class OverlayForm : Form
{
    private readonly Font _font = new("맑은 고딕", 10f, FontStyle.Bold);
    private readonly Font _small = new("맑은 고딕", 8f);
    private List<(string text, Color color)> _lines = [("대기 중", Theme.Text)];
    private List<(double t, double v)> _points = new();
    private double _t0, _t1;
    private string _graphTitle = "";
    private const int Pad = 10, LineGap = 2, GraphW = 240, GraphH = 70, GraphTop = 6, GraphLabelH = 16;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>true = 그래프 모드</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool GraphMode { get; private set; }

    /// <summary>오른쪽 클릭으로 모드가 바뀌었을 때</summary>
    public event EventHandler? ModeToggled;

    public OverlayForm()
    {
        CaptureGuard.Attach(this); // 화면 캡처에서 제외 (게임 위에 겹쳐도 인식에 섞이지 않게)
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.BgTertiary;
        Opacity = 0.9;
        DoubleBuffered = true;
        MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) { SetGraphMode(!GraphMode); ModeToggled?.Invoke(this, EventArgs.Empty); }
            else DragMove(e);
        };
        Relayout();
    }

    // 표시될 때 활성화하지 않음 (게임 창 포커스 유지)
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x00000008 /* WS_EX_TOPMOST */;
            return cp;
        }
    }

    /// <summary>게임 창을 클릭해도 가려지지 않도록 최상위로 다시 올림 (포커스는 가져오지 않음)</summary>
    public void KeepOnTop()
    {
        if (!Visible || !IsHandleCreated) return;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        SetWindowPos(Handle, (IntPtr)(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    public void SetGraphMode(bool on)
    {
        if (GraphMode == on) return;
        GraphMode = on;
        Relayout();
        Invalidate();
    }

    /// <summary>텍스트 모드 내용: 줄 단위로 내용과 색 지정 (같으면 다시 그리지 않음)</summary>
    public void SetLines(List<(string text, Color color)> lines)
    {
        if (lines.SequenceEqual(_lines)) return;
        _lines = lines;
        Relayout();
        Invalidate();
    }

    /// <summary>그래프 모드 내용: 위쪽 값 줄 + 그래프 점(t0~t1 구간)</summary>
    public void SetGraph(List<(string text, Color color)> lines, string title, List<(double t, double v)> points, double t0, double t1)
    {
        _lines = lines;
        _graphTitle = title;
        _points = points;
        _t0 = t0; _t1 = t1;
        Relayout(); // 크기가 같으면 실제로는 바뀌지 않음
        Invalidate();
    }

    /// <summary>현재 표시 중인 내용 (확인/시험용)</summary>
    public IReadOnlyList<(string text, Color color)> Lines => _lines;

    private int LineHeight => TextRenderer.MeasureText("가", _font).Height;

    private void Relayout()
    {
        int w = _lines.Count == 0 ? 50 : _lines.Max(l => TextRenderer.MeasureText(l.text, _font).Width);
        int h = _lines.Count * (LineHeight + LineGap) - LineGap;
        h = Math.Max(h, LineHeight);
        if (GraphMode)
        {
            w = Math.Max(w, GraphW);
            h += GraphTop + GraphLabelH + GraphH;
        }
        ClientSize = new Size(w + Pad * 2, h + Pad * 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int y = Pad;
        foreach (var (text, color) in _lines)
        {
            TextRenderer.DrawText(e.Graphics, text, _font, new Point(Pad, y), color, TextFormatFlags.NoPadding);
            y += LineHeight + LineGap;
        }
        if (GraphMode) DrawGraph(e.Graphics, new Rectangle(Pad, y - LineGap + GraphTop, ClientSize.Width - Pad * 2, GraphLabelH + GraphH));
    }

    /// <summary>작은 그래프: 제목·최댓값(위), 선 + 옅은 면, 0 기준선</summary>
    private void DrawGraph(Graphics g, Rectangle area)
    {
        var plot = new Rectangle(area.Left, area.Top + GraphLabelH, area.Width, GraphH);
        TextRenderer.DrawText(g, _graphTitle, _small, new Point(area.Left, area.Top), Theme.Muted, TextFormatFlags.NoPadding);
        using (var axis = new Pen(Theme.Line)) g.DrawLine(axis, plot.Left, plot.Bottom, plot.Right, plot.Bottom);

        if (_points.Count < 2 || _t1 <= _t0)
        {
            TextRenderer.DrawText(g, "사냥 시작 후 표시", _small, plot, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        double max = Math.Max(_points.Max(p => p.v), 1);
        string maxText = "최고 " + Abbrev(max);
        var ms = TextRenderer.MeasureText(maxText, _small);
        TextRenderer.DrawText(g, maxText, _small, new Point(area.Right - ms.Width, area.Top), Theme.Muted, TextFormatFlags.NoPadding);

        double hi = max * 1.1; // 맨 위에 붙지 않게 여유
        var pts = _points.Select(p => new PointF(
            (float)(plot.Left + (p.t - _t0) / (_t1 - _t0) * plot.Width),
            (float)(plot.Bottom - Math.Max(0, p.v) / hi * plot.Height))).ToArray();

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = pts.Append(new PointF(pts[^1].X, plot.Bottom)).Append(new PointF(pts[0].X, plot.Bottom)).ToArray();
        using (var b = new SolidBrush(Color.FromArgb(45, Theme.Blue))) g.FillPolygon(b, fill);
        using (var p = new Pen(Theme.Blue, 2) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts);
        using (var dot = new SolidBrush(Theme.Blue)) g.FillEllipse(dot, pts[^1].X - 3.5f, pts[^1].Y - 3.5f, 7, 7);
        g.SmoothingMode = SmoothingMode.None;
    }

    private static string Abbrev(double v) =>
        v >= 1e8 ? $"{v / 1e8:0.##}억" : v >= 1e4 ? $"{v / 1e4:#,0.#}만" : v.ToString("#,0");

    private void DragMove(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }
}
