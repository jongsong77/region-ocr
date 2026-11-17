using System.Runtime.InteropServices;

namespace RegionOcr;

/// <summary>디스코드 스타일 다크 테마 (색상 팔레트 + 컨트롤 일괄 적용)</summary>
public static class Theme
{
    // 배경 3단계
    public static readonly Color Bg = Color.FromArgb(0x31, 0x33, 0x38);        // 기본 배경
    public static readonly Color BgSecondary = Color.FromArgb(0x2B, 0x2D, 0x31); // 패널/목록
    public static readonly Color BgTertiary = Color.FromArgb(0x1E, 0x1F, 0x22);  // 입력칸/오버레이
    public static readonly Color Hover = Color.FromArgb(0x40, 0x42, 0x49);
    public static readonly Color Line = Color.FromArgb(0x3F, 0x41, 0x47);

    // 글자
    public static readonly Color Text = Color.FromArgb(0xDB, 0xDE, 0xE1);
    public static readonly Color Muted = Color.FromArgb(0x94, 0x9B, 0xA4);
    public static readonly Color Header = Color.FromArgb(0xF2, 0xF3, 0xF5);

    // 강조
    public static readonly Color Accent = Color.FromArgb(0x58, 0x65, 0xF2);      // 보라
    public static readonly Color AccentHover = Color.FromArgb(0x47, 0x52, 0xC4);
    public static readonly Color Green = Color.FromArgb(0x23, 0xA5, 0x5A);
    public static readonly Color GreenHover = Color.FromArgb(0x1A, 0x84, 0x48);
    public static readonly Color Red = Color.FromArgb(0xDA, 0x37, 0x3C);
    public static readonly Color RedHover = Color.FromArgb(0xA1, 0x28, 0x28);
    public static readonly Color Blue = Color.FromArgb(0x00, 0xA8, 0xFC);
    public static readonly Color Yellow = Color.FromArgb(0xF0, 0xB2, 0x32);
    public static readonly Color ButtonBg = Color.FromArgb(0x4E, 0x50, 0x58);
    public static readonly Color ButtonHover = Color.FromArgb(0x6D, 0x6F, 0x78);

    // 경험치 효율 표시용 파스텔 톤 (어두운 배경에서 눈부시지 않게)
    public static readonly Color PastelRed = Color.FromArgb(0xF4, 0xA7, 0xA7);
    public static readonly Color PastelOrange = Color.FromArgb(0xF7, 0xC6, 0x9C);
    public static readonly Color PastelYellow = Color.FromArgb(0xF3, 0xE2, 0xA2);
    public static readonly Color PastelEmerald = Color.FromArgb(0x9F, 0xE2, 0xC8);

    /// <summary>시간당 경험치 색: 150억 초과 에메랄드 / 100억~150억 빨강 / 50억~100억 주황 / 그 아래 노랑</summary>
    public static Color PerHourColor(double v) =>
        v > 15_000_000_000d ? PastelEmerald :
        v > 10_000_000_000d ? PastelRed :
        v >= 5_000_000_000d ? PastelOrange : PastelYellow;

    /// <summary>분당 경험치 색: 시간당 기준의 1/60 (분당 × 60 을 시간당 기준으로 판정)</summary>
    public static Color PerMinuteColor(double v) => PerHourColor(v * 60);

    public static readonly Font Font = new("맑은 고딕", 9f);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    /// <summary>창 제목 표시줄을 다크 모드로 (Windows 10 2004+ / 11)</summary>
    public static void DarkTitleBar(Form f)
    {
        int on = 1;
        if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int)); // 구버전 Windows 10
    }

    /// <summary>하위 컨트롤 전체에 테마 적용 (개별로 강조색을 준 컨트롤은 Tag="keep"으로 제외)</summary>
    public static void Apply(Control root)
    {
        StyleOne(root);
        foreach (Control c in root.Controls) Apply(c);
    }

    private static void StyleOne(Control c)
    {
        if (Equals(c.Tag, "keep")) return;
        switch (c)
        {
            case Form f:
                f.BackColor = Bg; f.ForeColor = Text;
                break;
            case Button b:
                StyleButton(b, ButtonBg, ButtonHover);
                break;
            case KeyCaptureBox k:
                k.BackColor = BgTertiary; k.ForeColor = Header; k.BorderStyle = BorderStyle.FixedSingle;
                break;
            case TextBox t:
                t.BackColor = BgTertiary; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle;
                break;
            case NumericUpDown n:
                n.BackColor = BgTertiary; n.ForeColor = Text; n.BorderStyle = BorderStyle.FixedSingle;
                break;
            case CheckBox cb:
                cb.ForeColor = Text; cb.FlatStyle = FlatStyle.Flat;
                cb.FlatAppearance.CheckedBackColor = Accent;
                cb.FlatAppearance.BorderColor = Muted;
                break;
            case DataGridView g:
                StyleGrid(g);
                break;
            case PictureBox p:
                p.BackColor = BgTertiary;
                break;
            case GroupBox gb:
                gb.ForeColor = Muted; gb.BackColor = Bg;
                break;
            case Label l:
                if (l.ForeColor == SystemColors.ControlText) l.ForeColor = Text;
                l.BackColor = Color.Transparent;
                break;
            default:
                c.BackColor = c.Parent?.BackColor ?? Bg;
                c.ForeColor = Text;
                break;
        }
    }

    public static void StyleButton(Button b, Color bg, Color hover)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = bg;
        b.ForeColor = Color.White;
        b.FlatAppearance.MouseOverBackColor = hover;
        b.FlatAppearance.MouseDownBackColor = hover;
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
        b.Padding = new Padding(6, 2, 6, 2);
    }

    private static void StyleGrid(DataGridView g)
    {
        g.BackgroundColor = BgSecondary;
        g.BorderStyle = BorderStyle.None;
        g.GridColor = Line;
        g.EnableHeadersVisualStyles = false;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.ColumnHeadersDefaultCellStyle.BackColor = BgTertiary;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = BgTertiary;
        g.ColumnHeadersHeight = 28;
        g.DefaultCellStyle.BackColor = BgSecondary;
        g.DefaultCellStyle.ForeColor = Text;
        g.DefaultCellStyle.SelectionBackColor = Hover;
        g.DefaultCellStyle.SelectionForeColor = Header;
        g.RowTemplate.Height = 26;
        foreach (DataGridViewColumn col in g.Columns)
            if (col is DataGridViewComboBoxColumn cb)
            {
                cb.FlatStyle = FlatStyle.Flat;
                cb.DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing; // 편집할 때만 드롭다운 표시
                cb.DefaultCellStyle.BackColor = BgSecondary;
                cb.DefaultCellStyle.ForeColor = Text;
            }
    }
}
