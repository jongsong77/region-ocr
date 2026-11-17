using System.Runtime.InteropServices;

namespace RegionOcr;

/// <summary>
/// 프로그램 창을 화면 캡처에서 제외 (Windows 10 2004+ WDA_EXCLUDEFROMCAPTURE).
/// 화면에는 그대로 보이지만 캡처에는 창 뒤 게임 화면이 찍힘.
/// 이유: 스킬 설정 창(실시간 미리보기)·오버레이·그래프가 게임 화면 위에 있으면 캡처에 같이 찍혀
///  - 미리보기가 다시 미리보기에 찍히는 거울 현상(무한 반복)
///  - 미리보기 속 "N초" 글자를 애드온 창으로 잘못 추적, 오버레이 글자가 MP/EXP 영역에 섞임
/// 부작용: 사용자의 스크린샷·녹화·방송에도 이 창들이 나오지 않음.
/// </summary>
public static class CaptureGuard
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private static bool _failLogged;

    /// <summary>창 핸들이 만들어지면(이미 있으면 바로) 캡처 제외 적용</summary>
    public static void Attach(Form f)
    {
        if (f.IsHandleCreated) Exclude(f);
        f.HandleCreated += (_, _) => Exclude(f); // 핸들이 다시 만들어지는 경우(스타일 변경 등)도 적용
    }

    private static void Exclude(Form f)
    {
        if (SetWindowDisplayAffinity(f.Handle, WDA_EXCLUDEFROMCAPTURE) || _failLogged) return;
        _failLogged = true;
        AppLog.Write($"창 캡처 제외 실패 (Windows 10 2004 이상 필요, 오류 {Marshal.GetLastWin32Error()}) → 프로그램 창이 게임 화면을 가리면 인식이 섞일 수 있음");
    }
}
