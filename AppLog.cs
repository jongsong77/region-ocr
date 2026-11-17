namespace RegionOcr;

/// <summary>동작 기록 (logs\events_날짜.log): 시작/일시정지/중지, 단축키 등록·입력, 기준 EXP 확정 등</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static void Write(string msg)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(Path.Combine(dir, $"events_{DateTime.Now:yyyyMMdd}.log"), line);
        }
        catch { /* 기록 실패는 무시 */ }
    }
}
