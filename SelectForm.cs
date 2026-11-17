namespace RegionOcr;

/// <summary>전체 화면(모든 모니터)을 덮고 마우스 드래그로 영역을 지정하는 오버레이</summary>
public sealed class SelectForm : Form
{
    private readonly Bitmap _shot;
    private readonly Rectangle _virtual;
    private Point _start;
    private Rectangle _sel;
    private bool _dragging;

    /// <summary>선택된 영역 (화면 물리 픽셀 좌표)</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Rectangle Selected { get; private set; }

    public SelectForm()
    {
        _virtual = SystemInformation.VirtualScreen;
        _shot = new Bitmap(_virtual.Width, _virtual.Height);
        using (var g = Graphics.FromImage(_shot))
            g.CopyFromScreen(_virtual.Location, Point.Empty, _virtual.Size);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _virtual;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _virtual; // 배율이 다른 모니터에 걸쳐도 크기 유지
        Activate();
    }

    // 모니터 간 DPI 변경으로 폼이 리사이즈되지 않도록 막음
    protected override void OnDpiChanged(DpiChangedEventArgs e) => e.Cancel = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; Close(); return; }
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _start = e.Location;
        _sel = Rectangle.Empty;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        _sel = Rectangle.FromLTRB(
            Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y),
            Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left) return;
        _dragging = false;
        if (_sel.Width < 5 || _sel.Height < 5) { _sel = Rectangle.Empty; Invalidate(); return; }
        Selected = new Rectangle(PointToScreen(_sel.Location), _sel.Size);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImageUnscaled(_shot, 0, 0);

        // 선택 영역 외부를 어둡게
        using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
        using (var region = new Region(ClientRectangle))
        {
            if (!_sel.IsEmpty) region.Exclude(_sel);
            g.FillRegion(dim, region);
        }

        using var font = new Font("맑은 고딕", 11f, FontStyle.Bold);
        if (_sel.IsEmpty)
        {
            g.DrawString("드래그로 영역 지정 / ESC·우클릭 취소", font, Brushes.Yellow, 20, 20);
            return;
        }
        using var pen = new Pen(Color.Red, 2);
        g.DrawRectangle(pen, _sel);
        g.DrawString($"{_sel.Width} x {_sel.Height}", font, Brushes.Yellow, _sel.X, Math.Max(0, _sel.Y - 22));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _shot.Dispose();
        base.Dispose(disposing);
    }
}
