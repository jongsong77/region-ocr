namespace RegionOcr;

/// <summary>
/// 스킬 규칙 판정. 쿨타임 1회당 1번만 입력.
/// - N초 &gt; 0: 남은 시간이 N초 이하로 확정되는 순간 입력
/// - N초 = 0: 마지막으로 본 값이 2초 이하였던 스킬이 창에서 사라질 때(쿨 종료) 입력
/// 남은 시간이 다시 늘어나거나(재사용) 쿨 종료로 사라지면 다음 쿨타임을 위해 재무장.
/// 3초 이상 남았는데 안 보이면(창 이동/가림) 상태를 유지해 다시 보였을 때 중복 입력하지 않음.
/// </summary>
public sealed class SkillAutomator
{
    private sealed class State
    {
        public int? LastSec;           // 확정된 마지막 남은 시간
        public int PendingSec = -1;    // 확정 대기 값 (같은 값 2회 연속 → 확정)
        public int PendingCount;
        public bool Armed = true;      // 이번 쿨타임에 아직 입력 안 함
        public int Absent;             // 연속으로 안 보인 횟수
        public DateTime LastSeenAt;    // 마지막으로 확정값을 본 시각
    }

    private readonly Dictionary<SkillRule, State> _st = new();

    public void Reset()
    {
        _st.Clear();
    }

    /// <summary>인식 결과로 규칙 판정. 반환: 지금 키를 입력할 규칙</summary>
    /// <param name="complete">이번 읽기에서 모든 줄을 판독했는지. false면 안 보이는 스킬을 '사라짐'으로 처리하지 않음</param>
    public List<SkillRule> Evaluate(IEnumerable<SkillRule> rules, List<CooldownLine> lines, bool complete = true)
    {
        var fire = new List<SkillRule>();
        foreach (var rule in rules)
        {
            if (!_st.TryGetValue(rule, out var st)) _st[rule] = st = new State();
            if (!rule.Enabled) { rule.Status = "꺼짐"; continue; }

            var line = FindLine(rule, lines);
            if (line is not null)
            {
                st.Absent = 0;
                if (line.Seconds == st.PendingSec) st.PendingCount++;
                else { st.PendingSec = line.Seconds; st.PendingCount = 1; }
                if (st.PendingCount < 2) continue; // 오인식 방지

                int sec = line.Seconds;
                if (st.LastSec is int prev && sec > prev + 1) st.Armed = true; // 재사용 → 새 쿨타임
                st.LastSec = sec;
                st.LastSeenAt = DateTime.Now;
                rule.Status = st.Armed ? $"{sec}초" : $"{sec}초 (입력함)";
                if (st.Armed && rule.Seconds > 0 && sec <= rule.Seconds) fire.Add(rule);
            }
            else
            {
                if (!complete) continue; // 판독 못 한 줄이 이 스킬일 수 있음 → 보류
                st.PendingSec = -1;
                st.PendingCount = 0;
                if (st.LastSec is null) { rule.Status = "대기"; continue; }
                if (++st.Absent < 2) continue;

                // 남은 시간이 3초 이상이었는데 안 보임 → 창 이동/가림으로 보고 상태 유지 (다시 보이면 이어서 판정, 중복 입력 방지).
                // 30초 넘게 안 보이면 초기화
                if (st.LastSec > 2 && DateTime.Now - st.LastSeenAt < TimeSpan.FromSeconds(30)) { rule.Status = $"안 보임 ({st.LastSec}초)"; continue; }

                // 쿨 종료로 사라짐 (마지막 값이 2초 이하)
                if (st.Armed && rule.Seconds == 0 && st.LastSec <= 2) fire.Add(rule);
                st.LastSec = null;
                st.Armed = true;
                rule.Status = "대기";
            }
        }
        return fire;
    }

    /// <summary>키 입력 완료 → 이번 쿨타임 동안 다시 입력하지 않음</summary>
    public void MarkFired(SkillRule rule)
    {
        if (_st.TryGetValue(rule, out var st) && st.LastSec is not null)
        {
            st.Armed = false;
            rule.Status = $"{st.LastSec}초 (입력함)";
        }
    }

    /// <summary>
    /// 규칙에 해당하는 줄.
    /// 1) 이름 모양이 등록돼 있으면 모양 유사도 0.8 이상 중 최고
    /// 2) 모양으로 못 찾으면: 한글 이름(OCR)이 규칙 이름과 같거나 자모 유사도 0.85 이상이고 모양 유사도도 0.45 이상인 줄
    ///    (게임 창 크기가 바뀌어 모양 크기가 달라 비교가 안 되면 이름이 완전히 같을 때만).
    ///    애드온 창은 반투명이라 줄 위치(뒤 배경)나 게임 창 크기가 바뀌면 같은 스킬도 모양이 달라짐
    ///    (녹화 시험: 1번째 줄 1.000, 2번째 0.524, 3번째 0.768 / 다른 스킬과는 최고 0.372) → 이름으로 보완
    /// 3) 모양 없이 직접 추가한 규칙: 이름 일치 또는 자모 유사도 0.85 이상
    /// </summary>
    public static CooldownLine? FindLine(SkillRule rule, List<CooldownLine> lines)
    {
        lines = lines.Where(l => l.Readable).ToList(); // 숫자 판독 불가 줄은 판정에 쓰지 않음 (complete=false로 보류됨)
        string a = CooldownReader.NormalizeName(rule.OcrName), b = CooldownReader.NormalizeName(rule.Name);
        double NameScore(CooldownLine l) =>
            l.Name.Length == 0 ? 0 : (l.Name == a || l.Name == b) ? 1 : Math.Max(Hangul.Similarity(l.Name, a), Hangul.Similarity(l.Name, b));

        if (rule.NameKey.Length > 0)
        {
            var byShape = lines.Select(l => (l, s: CooldownReader.NameSimilarity(rule.NameKey, l.NameKey)))
                               .Where(x => x.s >= 0.8).OrderByDescending(x => x.s).Select(x => x.l).FirstOrDefault();
            if (byShape is not null) return byShape;
            if (a.Length == 0 && b.Length == 0) return null;
            return lines.Select(l => (l, n: NameScore(l), s: CooldownReader.NameSimilarity(rule.NameKey, l.NameKey)))
                        // 크기가 2px 넘게 달라(게임 창 크기 변경) 모양 비교가 안 되면(유사도 0) 이름이 완전히 같을 때만 인정
                        .Where(x => x.n >= 0.85 && (x.s >= 0.45 || (x.s == 0 && x.n >= 1)))
                        .OrderByDescending(x => x.n).ThenByDescending(x => x.s).Select(x => x.l).FirstOrDefault();
        }

        if (a.Length == 0 && b.Length == 0) return null;
        return lines.Select(l => (l, n: NameScore(l))).Where(x => x.n >= 0.85)
                    .OrderByDescending(x => x.n).Select(x => x.l).FirstOrDefault();
    }
}

/// <summary>한글 자모 단위 유사도 (OCR이 받침/모음 하나를 틀린 경우 허용)</summary>
public static class Hangul
{
    public static string ToJamo(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length * 3);
        foreach (char c in s)
        {
            if (c is >= '가' and <= '힣')
            {
                int i = c - '가';
                sb.Append((char)(0x1100 + i / 588));
                sb.Append((char)(0x1161 + i % 588 / 28));
                if (i % 28 > 0) sb.Append((char)(0x11A7 + i % 28));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>1 - 편집거리/긴 길이 (0~1)</summary>
    public static double Similarity(string x, string y)
    {
        if (x.Length == 0 || y.Length == 0) return 0;
        string a = ToJamo(x), b = ToJamo(y);
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return 1 - (double)d[b.Length] / Math.Max(a.Length, b.Length);
    }
}
