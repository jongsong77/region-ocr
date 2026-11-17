using System.Runtime.InteropServices;

namespace RegionOcr;

/// <summary>키 이름 문자열 ↔ Keys 변환 ("Ctrl+Shift+F9", "F", "5" 등)</summary>
public static class KeyUtil
{
    public static string Format(Keys k)
    {
        var parts = new List<string>();
        if (k.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (k.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (k.HasFlag(Keys.Shift)) parts.Add("Shift");
        var code = k & Keys.KeyCode;
        if (code != Keys.None)
            parts.Add(code is >= Keys.D0 and <= Keys.D9 ? ((char)('0' + (code - Keys.D0))).ToString() : code.ToString());
        return string.Join("+", parts);
    }

    public static Keys Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Keys.None;
        Keys result = Keys.None;
        foreach (var p in s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) result |= Keys.Control;
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) result |= Keys.Alt;
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) result |= Keys.Shift;
            else if (p.Length == 1 && char.IsDigit(p[0])) result |= Keys.D0 + (p[0] - '0');
            else if (Enum.TryParse<Keys>(p, true, out var k)) result |= k;
        }
        return result;
    }
}

/// <summary>클릭 후 키를 누르면 그 키가 지정되는 입력칸 (Delete/Backspace = 해제)</summary>
public sealed class KeyCaptureBox : TextBox
{
    [System.ComponentModel.DefaultValue(false)]
    public bool AllowModifiers { get; set; }
    public event EventHandler? KeyAssigned;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Keys AssignedKey { get; private set; }

    public KeyCaptureBox()
    {
        ReadOnly = true;
        BackColor = SystemColors.Window;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
        ShortcutsEnabled = false;
    }

    public void SetKey(Keys k)
    {
        AssignedKey = k;
        Text = k == Keys.None ? "(없음)" : KeyUtil.Format(k);
    }

    protected override bool IsInputKey(Keys keyData) => true; // 방향키/Tab 등도 받기

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
        var code = e.KeyCode;
        if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return; // 수식키 단독은 대기
        Keys k = code is Keys.Delete or Keys.Back ? Keys.None : code | (AllowModifiers ? e.Modifiers : Keys.None);
        SetKey(k);
        KeyAssigned?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>전역 단축키 (다른 창/게임이 활성이어도 동작)</summary>
public static class GlobalHotkey
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    public const int WM_HOTKEY = 0x0312;

    public static bool Register(IntPtr hwnd, int id, Keys k)
    {
        UnregisterHotKey(hwnd, id);
        if ((k & Keys.KeyCode) == Keys.None) return true; // 해제 상태
        uint mods = 0x4000; // MOD_NOREPEAT: 누르고 있어도 한 번만
        if (k.HasFlag(Keys.Alt)) mods |= 0x1;
        if (k.HasFlag(Keys.Control)) mods |= 0x2;
        if (k.HasFlag(Keys.Shift)) mods |= 0x4;
        return RegisterHotKey(hwnd, id, mods, (uint)(k & Keys.KeyCode));
    }

    public static void Unregister(IntPtr hwnd, int id) => UnregisterHotKey(hwnd, id);
}
