namespace RegionOcr;

/// <summary>사냥 중 경험치 증가량 집계 (일시정지 동안의 변화는 제외)</summary>
public sealed class HuntTracker
{
    private long? _pending;
    private int _pendingCount;
    private TimeSpan _elapsed;
    private DateTime _resumedAt;

    public bool Active { get; private set; }
    public bool Started => Active || _elapsed > TimeSpan.Zero;

    /// <summary>확정된 현재 경험치</summary>
    public long? Current { get; private set; }

    /// <summary>사냥 시작(또는 초기화 후 시작) 시점에 확정된 경험치. 일시정지/재개로는 바뀌지 않음</summary>
    public long? StartValue { get; private set; }

    /// <summary>사냥(활성) 중 얻은 누적 경험치</summary>
    public long Gained { get; private set; }

    /// <summary>시각(정시 단위)별 누적 경험치</summary>
    public SortedDictionary<DateTime, long> Hourly { get; } = new();

    /// <summary>그래프용 기록: (사냥 경과 초, 누적 경험치). 일시정지 시간은 제외, 값이 바뀌지 않는 구간은 양 끝만 유지</summary>
    public List<(double sec, long gained)> Samples { get; } = new();

    public TimeSpan Elapsed => _elapsed + (Active ? DateTime.Now - _resumedAt : TimeSpan.Zero);

    /// <summary>경과 sec 시점의 누적 경험치 (그 시점 이전의 마지막 기록값)</summary>
    public long GainedAt(double sec)
    {
        int lo = 0, hi = Samples.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Samples[mid].sec <= sec) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found < 0 ? 0 : Samples[found].gained;
    }

    /// <summary>경과 sec 시점의 분당 경험치 (직전 window초 동안 얻은 양 기준, 5초 미만이면 null)</summary>
    public double? RateAt(double sec, double window = 60)
    {
        if (sec < 5) return null;
        double from = Math.Max(0, sec - window);
        return (GainedAt(sec) - GainedAt(from)) / (sec - from) * 60;
    }

    private void AddSample()
    {
        double sec = Elapsed.TotalSeconds;
        int n = Samples.Count;
        // 같은 값이 이어지면 마지막 점의 시각만 갱신 (점 수 절약)
        if (n >= 2 && Samples[n - 1].gained == Gained && Samples[n - 2].gained == Gained) Samples[n - 1] = (sec, Gained);
        else Samples.Add((sec, Gained));
    }
    public double PerMinute => Elapsed.TotalSeconds >= 5 ? Gained / Elapsed.TotalMinutes : 0;
    public double PerHour => PerMinute * 60;

    /// <summary>시작/재개 후 첫 확정값을 기준으로만 잡고 증가량에 넣지 않음 (일시정지 동안 오른 경험치 제외)</summary>
    private bool _rebase;

    /// <summary>진단 로그 (기준값 확정 등)</summary>
    public event Action<string>? Log;

    public void Start()
    {
        if (Active) return;
        Active = true;
        _resumedAt = DateTime.Now;
        // 현재 경험치를 다시 읽어 기준으로 삼음 (이전 확정값/읽는 중이던 값은 버림)
        _rebase = true;
        _pending = null;
        _pendingCount = 0;
        AddSample();
    }

    public void Pause()
    {
        if (!Active) return;
        _elapsed += DateTime.Now - _resumedAt;
        Active = false;
    }

    public void Reset()
    {
        Active = false;
        _elapsed = TimeSpan.Zero;
        Gained = 0;
        StartValue = null;
        Current = null;
        _pending = null;
        _pendingCount = 0;
        _rebase = false;
        Hourly.Clear();
        Samples.Clear();
    }

    /// <summary>읽은 경험치 입력. 같은 값이 2회 연속 읽혀야 확정(오인식 방지)</summary>
    public void Feed(long value)
    {
        if (_pending == value) _pendingCount++;
        else { _pending = value; _pendingCount = 1; }
        if (_pendingCount < 2) return;

        if (Active && _rebase)
        {
            _rebase = false;
            Log?.Invoke($"기준 EXP 확정 {value:#,0}" + (Current is long old && old != value ? $" (정지 중 변화 {value - old:+#,0;-#,0} 제외)" : ""));
        }
        else if (Active && Current is long prev && value != prev)
        {
            long delta = value - prev;
            Gained += delta;
            Log?.Invoke($"EXP {prev:#,0} → {value:#,0} ({delta:+#,0;-#,0}) 누적 {Gained:+#,0;-#,0;0}");
            var now = DateTime.Now;
            var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            Hourly[hour] = Hourly.GetValueOrDefault(hour) + delta;
        }
        Current = value;
        if (Active)
        {
            StartValue ??= value; // 시작 후 처음 확정된 값
            AddSample();
        }
    }
}
