using System.Drawing.Drawing2D;

namespace RegionOcr;

/// <summary>
/// 사냥 경험치 실시간 그래프. 가로축 = 사냥 경과 시간(일시정지 제외).
/// 위: 누적 경험치, 아래: 분당 경험치(직전 60초 기준). 두 값은 단위가 달라 차트를 나눔.
/// 마우스를 올리면 그 시점의 값 표시.
/// </summary>
public sealed class GraphForm : Form
{
    private readonly HuntTracker _hunt;
    private readonly GraphCanvas _canvas = new();
    private readonly List<Button> _rangeButtons = new();
    private double _range = 300; // 표시 구간(초), 0 = 전체
    private Point? _mouse;

    private static readonly (string label, double sec)[] Ranges = [("1분", 60), ("5분", 300), ("30분", 1800), ("전체", 0)];

    public GraphForm(HuntTracker hunt)
    {
        _hunt = hunt;
        Text = "경험치 그래프";
        Font = Theme.Font;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        MinimumSize = new Size(380, 300);
        ClientSize = new Size(560, 380);

        // 위쪽 한 줄: 표시 구간 선택
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 5, 8, 0), WrapContents = false };
        bar.Controls.Add(new Label { Text = "구간", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        foreach (var (label, sec) in Ranges)
        {
            var b = new Button { Text = label, AutoSize = true, Margin = new Padding(0, 0, 4, 0), Tag = sec };
            b.Click += (_, _) => { _range = sec; UpdateRangeButtons(); _canvas.Invalidate(); };
            _rangeButtons.Add(b);
            bar.Controls.Add(b);
        }

        _canvas.Dock = DockStyle.Fill;
        _canvas.Paint += (_, e) => Draw(e.Graphics);
        _canvas.MouseMove += (_, e) => { _mouse = e.Location; _canvas.Invalidate(); };
        _canvas.MouseLeave += (_, _) => { _mouse = null; _canvas.Invalidate(); };
        _canvas.Resize += (_, _) => _canvas.Invalidate();

        Controls.Add(_canvas);
        Controls.Add(bar);
        Theme.Apply(this);
        _canvas.BackColor = Theme.BgTertiary;
        UpdateRangeButtons();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    // 닫기 = 숨기기 (다시 열 때 설정 유지)
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    /// <summary>1초마다 호출: 보이는 동안만 다시 그림</summary>
    public void Tick()
    {
        if (Visible && WindowState != FormWindowState.Minimized) _canvas.Invalidate();
    }

    private void UpdateRangeButtons()
    {
        foreach (var b in _rangeButtons)
            if ((double)b.Tag! == _range) Theme.StyleButton(b, Theme.Accent, Theme.AccentHover);
            else Theme.StyleButton(b, Theme.ButtonBg, Theme.ButtonHover);
    }

    // =====================================================================
    // 그리기
    // =====================================================================

    private const int PadL = 62, PadR = 14, PadTop = 30, Gap = 38, AxisH = 22;
    private static readonly Font LabelFont = new("맑은 고딕", 8.5f);
    private static readonly Font TitleFont = new("맑은 고딕", 9f, FontStyle.Bold);

    private void Draw(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var area = _canvas.ClientRectangle;

        double tEnd = _hunt.Elapsed.TotalSeconds;
        if (!_hunt.Started || _hunt.Samples.Count == 0)
        {
            TextRenderer.DrawText(g, "사냥을 시작하면(F9) 그래프가 그려집니다", Font, area, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        // 구간: 짧은 사냥은 0부터, 길면 최근 _range초
        double span = _range <= 0 ? Math.Max(tEnd, 60) : _range;
        double t0 = Math.Max(0, tEnd - span), t1 = Math.Max(t0 + span, tEnd);

        // 세로 배치: [제목] 위 차트 [간격+제목] 아래 차트 [시간 눈금]
        int plotH = (area.Height - PadTop - Gap - AxisH - 8) / 2;
        if (plotH < 30) return;
        var top = new Rectangle(PadL, PadTop, area.Width - PadL - PadR, plotH);
        var bottom = new Rectangle(PadL, top.Bottom + Gap, top.Width, plotH);
        if (top.Width < 60) return;

        // ---- 데이터: 누적 (기록 점 + 구간 시작/현재 시점), 분당 (일정 간격 표본)
        var cum = new List<(double t, double v)> { (t0, _hunt.GainedAt(t0)) };
        foreach (var (sec, gained) in _hunt.Samples)
            if (sec > t0 && sec < tEnd) cum.Add((sec, gained));
        cum.Add((tEnd, _hunt.Gained));

        int n = Math.Clamp(top.Width / 3, 20, 400);
        var rate = new List<(double t, double v)>();
        for (int i = 0; i <= n; i++)
        {
            double t = t0 + (tEnd - t0) * i / n;
            if (_hunt.RateAt(t) is double r) rate.Add((t, r));
        }

        double cumMin = cum.Min(p => p.v), cumMax = cum.Max(p => p.v);
        double rateMax = rate.Count > 0 ? rate.Max(p => p.v) : 0;

        DrawChart(g, top, "누적 EXP", $"+{Abbrev(_hunt.Gained)}", t0, t1, cumMin, cumMax, cum, Theme.Green, step: true, xLabels: false);
        double? nowRate = _hunt.RateAt(tEnd);
        DrawChart(g, bottom, "분당 EXP (최근 60초)", nowRate is double nr ? Abbrev(nr) : "-", t0, t1, 0, rateMax, rate, Theme.Blue, step: false, xLabels: true);

        // ---- 마우스 위치: 세로선 + 값 표시
        if (_mouse is Point m && m.X >= top.Left && m.X <= top.Right && m.Y >= top.Top && m.Y <= bottom.Bottom + AxisH)
        {
            double t = t0 + (t1 - t0) * (m.X - top.Left) / top.Width;
            if (t > tEnd) return;
            using var pen = new Pen(Theme.Muted, 1) { DashStyle = DashStyle.Dot };
            g.DrawLine(pen, m.X, top.Top, m.X, top.Bottom);
            g.DrawLine(pen, m.X, bottom.Top, m.X, bottom.Bottom);
            long gained = t >= tEnd ? _hunt.Gained : _hunt.GainedAt(t);
            string tip = $"{FormatTime(t)}\n누적 +{gained:#,0}\n분당 {(_hunt.RateAt(t) is double rr ? rr.ToString("#,0") : "-")}";
            var size = TextRenderer.MeasureText(tip, LabelFont);
            var box = new Rectangle(m.X + 12, m.Y + 12, size.Width + 12, size.Height + 8);
            if (box.Right > area.Right - 4) box.X = m.X - 12 - box.Width;
            if (box.Bottom > area.Bottom - 4) box.Y = m.Y - 12 - box.Height;
            using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, box);
            using (var border = new Pen(Theme.Line)) g.DrawRectangle(border, box);
            TextRenderer.DrawText(g, tip, LabelFont, new Point(box.X + 6, box.Y + 4), Theme.Text);
        }
    }

    private void DrawChart(Graphics g, Rectangle r, string title, string current, double t0, double t1,
        double vMin, double vMax, List<(double t, double v)> pts, Color color, bool step, bool xLabels)
    {
        // 제목(왼쪽) + 현재값(오른쪽)
        TextRenderer.DrawText(g, title, TitleFont, new Point(r.Left - PadL + 8, r.Top - 27), Theme.Muted);
        var cs = TextRenderer.MeasureText(current, TitleFont);
        TextRenderer.DrawText(g, current, TitleFont, new Point(r.Right - cs.Width, r.Top - 27), Theme.Header);

        // 세로축: 보기 좋은 간격으로 3~5칸
        double lo = vMin, hi = Math.Max(vMax, vMin + 1);
        double stepV = NiceStep((hi - lo) / 4);
        lo = Math.Floor(lo / stepV) * stepV;
        hi = Math.Ceiling(hi / stepV) * stepV;
        if (hi <= lo) hi = lo + stepV;

        float X(double t) => (float)(r.Left + (t - t0) / (t1 - t0) * r.Width);
        float Y(double v) => (float)(r.Bottom - (v - lo) / (hi - lo) * r.Height);

        using var grid = new Pen(Theme.Line, 1);
        for (double v = lo; v <= hi + stepV / 2; v += stepV)
        {
            float y = Y(v);
            g.DrawLine(grid, r.Left, y, r.Right, y);
            string s = Abbrev(v);
            var sz = TextRenderer.MeasureText(s, LabelFont);
            TextRenderer.DrawText(g, s, LabelFont, new Point(r.Left - sz.Width - 4, (int)y - sz.Height / 2), Theme.Muted);
        }

        if (xLabels)
        {
            double tStep = NiceTimeStep((t1 - t0) / 5);
            for (double t = Math.Ceiling(t0 / tStep) * tStep; t <= t1 + 0.01; t += tStep)
            {
                string s = FormatTime(t);
                var sz = TextRenderer.MeasureText(s, LabelFont);
                int x = Math.Clamp((int)X(t) - sz.Width / 2, r.Left - 10, r.Right - sz.Width + 6);
                TextRenderer.DrawText(g, s, LabelFont, new Point(x, r.Bottom + 4), Theme.Muted);
            }
        }

        if (pts.Count < 2) return;

        // 선 (누적은 계단형: 경험치는 사냥할 때마다 한 번에 오름)
        var line = new List<PointF>();
        for (int i = 0; i < pts.Count; i++)
        {
            if (step && i > 0) line.Add(new PointF(X(pts[i].t), Y(pts[i - 1].v)));
            line.Add(new PointF(X(pts[i].t), Y(pts[i].v)));
        }
        var fill = new List<PointF>(line) { new(line[^1].X, r.Bottom), new(line[0].X, r.Bottom) };
        using (var b = new SolidBrush(Color.FromArgb(40, color))) g.FillPolygon(b, fill.ToArray());
        using (var p = new Pen(color, 2) { LineJoin = LineJoin.Round }) g.DrawLines(p, line.ToArray());
        // 현재 시점 표시점
        using (var dot = new SolidBrush(color)) g.FillEllipse(dot, line[^1].X - 4, line[^1].Y - 4, 8, 8);
    }

    // ---- 눈금/표기

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double f = raw / mag;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * mag;
    }

    private static double NiceTimeStep(double raw)
    {
        foreach (var s in new double[] { 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800 })
            if (s >= raw) return s;
        return 21600;
    }

    private static string FormatTime(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>억/만 단위 축약 (예: 1.25억, 3,400만)</summary>
    private static string Abbrev(double v)
    {
        double a = Math.Abs(v);
        if (a >= 1e8) return $"{v / 1e8:0.##}억";
        if (a >= 1e4) return $"{v / 1e4:#,0.#}만";
        return v.ToString("#,0");
    }

    /// <summary>깜빡임 없는 그리기용 패널</summary>
    private sealed class GraphCanvas : Panel
    {
        public GraphCanvas()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }
}
