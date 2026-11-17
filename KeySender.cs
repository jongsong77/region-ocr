using System.Runtime.InteropServices;

namespace RegionOcr;

/// <summary>게임 창이 활성 상태일 때만 키 입력(SendInput, 스캔코드)</summary>
public static class KeySender
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;

    /// <summary>
    /// 현재 활성 창이 지정 좌표(게임 화면 안의 MP 영역)를 포함하고, 이 프로그램 창이 아니면 true
    /// </summary>
    public static bool IsGameForeground(Point screenPoint)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        if (!GetWindowRect(fg, out var r)) return false;
        return screenPoint.X >= r.Left && screenPoint.X < r.Right && screenPoint.Y >= r.Top && screenPoint.Y < r.Bottom;
    }

    /// <summary>키 누름 → 잠깐 유지 → 뗌 (게임이 인식하도록 스캔코드 사용)</summary>
    public static async Task PressAsync(Keys key)
    {
        ushort sc = (ushort)MapVirtualKey((uint)key, 0 /* MAPVK_VK_TO_VSC */);
        Send(sc, KEYEVENTF_SCANCODE);
        await Task.Delay(40);
        Send(sc, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP);
    }

    private static void Send(ushort scan, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }
}
