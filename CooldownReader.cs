using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RegionOcr;

/// <summary>애드온 창에서 읽은 한 줄</summary>
/// <param name="Name">표시용 이름 (OCR 결과, 게임 글꼴이라 부정확할 수 있음)</param>
/// <param name="Seconds">남은 초 (학습된 숫자 모양으로 판독)</param>
/// <param name="NameKey">이름 부분 픽셀 모양 지문 (규칙 비교용, 숫자가 바뀌어도 동일)</param>
/// <remarks>Seconds가 -1이면 숫자 판독 불가 (학습 안 된 숫자) → [숫자 학습] 대상</remarks>
public sealed record CooldownLine(string Name, int Seconds, string NameKey)
{
    public bool Readable => Seconds >= 0;
    public override string ToString() => Readable ? $"{Name}   {Seconds}초" : $"{Name}   ?초  ← 숫자 학습 필요";
}

/// <summary>
/// 게임 안 애드온(쿨타임) 창 인식.
/// 흰 글자만 남겨(키잉) 픽셀로 줄/단어를 나누고, 남은 초는 학습한 숫자 모양으로, 이름은 픽셀 모양 지문으로 비교.
/// 창 위치 탐색은 픽셀 줄 나누기 + "N초" 확인(숫자 템플릿 → PaddleOCR), 템플릿이 못 읽은 초와 표시용 이름은 PaddleOCR 한국어 모델(NameOcr).
/// Windows OCR은 쓰지 않아도 됨 (있으면 표시 이름 예비 판독에만 사용). 게임 픽셀 글꼴을 불안정하게 읽기 때문(무형검→무협겸, 40초→4초).
/// 창은 게임 안에서 옮길 수 있으므로 게임 화면 전체에서 "…초" 줄을 찾아 위치를 잡고 그 주변만 추적.
/// </summary>
public sealed class CooldownReader
{
    private readonly OcrService? _ocr; // 없어도 동작 (표시 이름 예비 판독에만 사용)
    // 게임 픽셀 글꼴은 같은 글자가 항상 같은 모양 → 엄격한 기준 (모르는 숫자를 닮은 숫자로 읽지 않도록)
    private readonly GlyphMatcher _digits = new("cooldown_templates.json") { MinScore = 0.9 };

    // 추적 영역 (게임 화면 기준 비율 좌표 0~1 → 게임 창 크기/위치가 바뀌어도 유지)
    private RectangleF? _roi;
    private DateTime _lastScanAt = DateTime.MinValue;

    private static readonly Regex LooseRx = new(@"^(?<name>.*?)\s*(?<sec>\d{1,4})\s*\S{0,2}$", RegexOptions.Compiled);
    private static readonly Regex SecRx = new(@"^(\d{1,4})초$", RegexOptions.Compiled);

    /// <summary>창을 못 찾았을 때 전체 화면 재탐색 간격 (전체 OCR 약 0.5~1초 소요)</summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>화면 캡처 함수 (테스트 시 저장 이미지로 대체)</summary>
    public Func<Rectangle, Bitmap> Capture { get; set; } = OcrService.Capture;

    /// <summary>최근 인식 결과</summary>
    public List<CooldownLine> Last { get; private set; } = new();

    /// <summary>최근 읽기에서 모든 줄을 판독했는지 (false면 판정 보류)</summary>
    public bool LastComplete { get; private set; }

    /// <summary>추적 중인 창 영역 (화면 좌표, 없으면 null)</summary>
    public Rectangle? WindowRect { get; private set; }

    /// <summary>마지막으로 인식에 쓴 이미지 (미리보기용, 흑백)</summary>
    public Bitmap? Preview { get; private set; }

    /// <summary>학습된 숫자 글자 목록 (예: "0123469초")</summary>
    public string LearnedChars => string.Concat(_digits.Templates.Select(t => t.Ch).Distinct().OrderBy(c => c));

    // 마지막 읽기의 줄별 숫자 부분 마스크 (수동 학습용)
    private readonly List<bool[,]> _lastNumMasks = new();

    /// <param name="ocr">Windows OCR (선택). 없거나 한국어 언어팩이 없어도 창 탐색·초 판독·이름 표시는 PaddleOCR로 동작</param>
    public CooldownReader(OcrService? ocr = null)
    {
        _ocr = ocr;
        _digits.Load();
    }

    public void Reset() { _roi = null; WindowRect = null; Last = new(); _lostSince = null; }

    // 창 이동으로 줄을 잃은 시각 (null = 정상)
    private DateTime? _lostSince;
    private static readonly TimeSpan LostTimeout = TimeSpan.FromSeconds(30);

    /// <summary>한 번 읽기. 반환: 읽은 줄 목록, 창을 못 찾았으면 null</summary>
    /// <param name="canvas">게임 화면(검은 여백 제외) 화면 좌표</param>
    public async Task<List<CooldownLine>?> ReadAsync(Rectangle canvas)
    {
        LastComplete = false;
        if (canvas.Width <= 0 || canvas.Height <= 0) return null;
        float s = canvas.Height / 720f;

        if (_roi is { } roi)
        {
            var r = ToScreen(roi, canvas);
            var (lines, complete, touchesEdge) = await ReadAreaAsync(r, s);
            if (lines.Count > 0 && !touchesEdge)
            {
                WindowRect = r;
                _lostSince = null;
                return Done(lines, complete); // 숫자 판독 불가 줄이 있으면 complete=false (판정 보류)
            }

            // 3초 이상 남은 쿨타임이 보이다가 줄이 모두 사라짐 → 창 이동으로 판단
            // (게임은 쿨이 끝날 때 0초까지 표시하므로 정상 종료와 구분됨). 창을 잃은 동안은 판정 보류
            if (lines.Count == 0 && _lostSince is null && Last.Any(l => l.Seconds > 2)) _lostSince = DateTime.Now;
            if (_lostSince is { } ls && DateTime.Now - ls > LostTimeout) _lostSince = null; // 오래 못 찾으면(창 닫힘 등) 빈 창으로 간주
            bool lost = _lostSince is not null;

            // 줄이 경계에 걸림(창 이동/줄 증가) 또는 창 이동 → 1초 간격, 줄 없음(쿨 없음) → 드물게 전체 재탐색
            var since = DateTime.Now - _lastScanAt;
            bool rescan = lines.Count > 0 || lost
                ? since >= TimeSpan.FromSeconds(1) || ScanInterval == TimeSpan.Zero
                : since >= ScanInterval * 2;
            if (rescan && await ScanAsync(canvas, s) is { } found)
            {
                _lostSince = null;
                return found;
            }
            WindowRect = r;
            if (lost) { LastComplete = false; return lines; } // Last 유지 (이동 판단 근거)
            return Done(lines, complete && !touchesEdge);
        }

        if (DateTime.Now - _lastScanAt < ScanInterval) return null;
        return await ScanAsync(canvas, s);
    }

    private List<CooldownLine> Done(List<CooldownLine> lines, bool complete)
    {
        Last = lines;
        LastComplete = complete;
        return lines;
    }

    /// <summary>
    /// 게임 화면 전체에서 "이름 N초" 줄 묶음을 찾아 추적 영역 설정 후 그 영역을 읽음.
    /// Windows OCR 없이 동작: 흰 글자 마스크를 픽셀로 줄·단어 덩어리로 나누고, 마지막 단어가 "N초"인지
    /// 숫자 템플릿 → (안 되면) PaddleOCR 인식 전용으로 확인. 확인 결과는 모양별로 기억해 같은 글자를 다시 OCR하지 않음.
    /// </summary>
    private async Task<List<CooldownLine>?> ScanAsync(Rectangle canvas, float s)
    {
        _lastScanAt = DateTime.Now;
        using var shot = Capture(canvas);
        var mask = await Task.Run(() => KeyWhite(shot));
        var segs = await Task.Run(() => ScanLines(mask, s));

        var cands = new List<RectangleF>();
        foreach (var p in segs)
            if (await IsCooldownNumberAsync(mask, shot, p))
                cands.Add(RectangleF.FromLTRB(p.NameL, p.Top, p.NumR + 1, p.Bottom + 1));
        if (cands.Count == 0) return null;

        // 왼쪽 정렬이 같은 줄끼리 묶어 가장 많은 묶음 선택
        float tol = 6f * s;
        var best = cands.GroupBy(c => cands.First(o => Math.Abs(o.X - c.X) <= tol).X)
                        .OrderByDescending(g => g.Count()).First().ToList();

        var union = best.Aggregate(RectangleF.Union);
        // 여유: 왼쪽/위 8, 오른쪽 60(긴 이름·자릿수), 아래 5줄(쿨타임 추가) — 기준 해상도 px
        var area = RectangleF.FromLTRB(union.Left - 8 * s, union.Top - 8 * s, union.Right + 60 * s, union.Bottom + 84 * s);
        area.Intersect(new RectangleF(0, 0, canvas.Width, canvas.Height));
        _roi = new RectangleF(area.X / canvas.Width, area.Y / canvas.Height, area.Width / canvas.Width, area.Height / canvas.Height);
        WindowRect = ToScreen(_roi.Value, canvas);

        var (lines, complete, edge) = await ReadAreaAsync(WindowRect.Value, s);
        return Done(lines, complete && !edge);
    }

    /// <summary>추적 영역 읽기</summary>
    /// <returns>줄 목록, 모든 줄 판독 여부, 글자 줄이 영역 경계에 닿음(잘렸을 가능성) 여부</returns>
    private async Task<(List<CooldownLine> lines, bool complete, bool touchesEdge)> ReadAreaAsync(Rectangle r, float s)
    {
        int scale = OcrScale(s);
        using var shot = Capture(r);
        var mask = await Task.Run(() => KeyWhite(shot));
        var parsed = ParseLines(mask, s);
        _lastNumMasks.Clear();
        if (parsed.Count == 0)
        {
            SetPreview(GlyphMatcher.ToBitmap(mask, scale, Pad));
            return (new(), true, false);
        }

        var keys = parsed.Select(p => NameKeyOf(Crop(mask, p.Top, p.Bottom, p.NameL, p.NameR, pad: 0))).ToList();
        var img = GlyphMatcher.ToBitmap(mask, scale, Pad);
        SetPreview(img);

        // 표시용 이름: OCR은 같은 글자도 매번 다르게 읽으므로, 처음 본 이름 모양만 OCR하고 그 결과를 계속 사용.
        // 1순위 PaddleOCR 한국어(원본 색상 이름 영역), 실패하면 아래 Windows OCR
        for (int i = 0; i < parsed.Count; i++)
        {
            if (CachedName(keys[i]) is not null) continue;
            var p = parsed[i];
            var nameRect = Rectangle.Intersect(new Rectangle(p.NameL - 3, p.Top - 3, p.NameR - p.NameL + 7, p.Bottom - p.Top + 7),
                                               new Rectangle(0, 0, shot.Width, shot.Height));
            using var rawName = shot.Clone(nameRect, PixelFormat.Format32bppArgb);
            string? name = await Task.Run(() => NameOcr.Read(rawName));
            if (string.IsNullOrEmpty(name)) continue;
            if (_nameCache.Count >= 200) _nameCache.Clear();
            _nameCache[keys[i]] = name;
        }
        if (_ocr is not null && keys.Any(k => CachedName(k) is null))
        {
            var raw = await _ocr.RecognizeLinesAsync(img);
            var ocrLines = raw.Select(l => (text: Clean(l.text), rect: Unscale(l.rect, scale))).ToList();
            for (int i = 0; i < parsed.Count; i++)
            {
                if (CachedName(keys[i]) is not null) continue;
                var p = parsed[i];
                var ocr = ocrLines.FirstOrDefault(o => o.rect.Y + o.rect.Height / 2 >= p.Top && o.rect.Y + o.rect.Height / 2 <= p.Bottom);
                var om = ocr.text is null ? null : LooseRx.Match(ocr.text);
                string name = om is { Success: true } ? NormalizeName(om.Groups["name"].Value) : "";
                if (name.Length == 0) continue;
                if (_nameCache.Count >= 200) _nameCache.Clear();
                _nameCache[keys[i]] = name;
            }
        }

        var lines = new List<CooldownLine>();
        bool complete = true, edge = false;
        int h = mask.GetLength(0), w = mask.GetLength(1);
        for (int i = 0; i < parsed.Count; i++)
        {
            var p = parsed[i];
            if (p.Top <= 1 || p.Bottom >= h - 2 || p.NameL <= 1 || p.NumR >= w - 2) edge = true;

            var num = Crop(mask, p.Top, p.Bottom, p.NumL, p.NumR, pad: 4);
            _lastNumMasks.Add(num);
            int? sec = ReadSeconds(num);
            if (sec is null) // 템플릿이 못 읽음(창 크기가 달라 글자가 흐려진 경우 등) → 원본 색상으로 한글 OCR
            {
                var numRect = Rectangle.Intersect(new Rectangle(p.NumL - 3, p.Top - 3, p.NumR - p.NumL + 7, p.Bottom - p.Top + 7),
                                                  new Rectangle(0, 0, shot.Width, shot.Height));
                sec = await ReadSecondsOcrAsync(num, shot, numRect);
            }
            if (sec is null) complete = false;

            // 판독 불가 줄도 목록에 포함 (줄 순서 = _lastNumMasks 순서 → 수동 학습에 사용)
            lines.Add(new CooldownLine(CachedName(keys[i]) ?? "(이름)", sec ?? -1, keys[i]));
        }
        return (lines, complete, edge);
    }

    // 이름 모양 → 처음 OCR로 읽은 표시 이름
    private readonly Dictionary<string, string> _nameCache = new();

    private string? CachedName(string key)
    {
        if (_nameCache.TryGetValue(key, out var n)) return n;
        // 배경이 비쳐 픽셀 몇 개가 달라진 경우도 같은 이름으로
        var hit = _nameCache.FirstOrDefault(kv => NameSimilarity(kv.Key, key) >= 0.9).Value;
        if (hit is not null) _nameCache[key] = hit; // 순회가 끝난 뒤 추가
        return hit;
    }

    /// <summary>
    /// 남은 초 판독: 학습한 숫자 모양으로만 읽음. 모르는 글자가 있으면 null (판정 보류, [숫자 학습] 대상).
    /// OCR 값으로 자동 학습하지 않음 — OCR이 169를 "163"으로 읽으면 9 모양을 3으로 배워 이후 계속 오판하기 때문.
    /// </summary>
    private int? ReadSeconds(bool[,] num)
    {
        if (_digits.Templates.Count == 0) return null;
        var m = SecRx.Match(_digits.Recognize(num));
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    // 숫자 칸 모양 → OCR 결과 (같은 숫자는 매번 같은 모양으로 그려지므로 다시 OCR하지 않음. 녹화 시험: 464줄 → 고유 모양 96개)
    private readonly Dictionary<string, int?> _secCache = new();

    /// <summary>템플릿이 못 읽은 남은 초를 PaddleOCR(인식 전용)로 읽음. 같은 숫자 모양은 캐시 사용</summary>
    private async Task<int?> ReadSecondsOcrAsync(bool[,] num, Bitmap shot, Rectangle numRect)
    {
        string key = NameKeyOf(num);
        if (_secCache.TryGetValue(key, out var cached)) return cached;
        if (numRect.Width <= 0 || numRect.Height <= 0) return null;
        using var raw = shot.Clone(numRect, PixelFormat.Format32bppArgb);
        string? digits = await Task.Run(() => NameOcr.ReadSecondsText(raw));
        // 템플릿 글자 나눔과 맞춰 봄: 글자 수 = 숫자 수 + 1('초'), 템플릿이 확실히 읽은 자리는 같은 숫자여야 함.
        // 안 맞으면 판독 보류 (잘못 읽은 값으로 키를 누르지 않도록)
        int? v = digits is not null && MatchesTemplate(digits, _digits.Templates.Count > 0 ? _digits.Recognize(num) : null)
            ? int.Parse(digits) : null;
        if (_secCache.Count >= 2000) _secCache.Clear();
        _secCache[key] = v;
        return v;
    }

    /// <summary>OCR 숫자와 템플릿 판독(예: "?1?" = 숫자 2개 + 초, 둘째 자리 1)이 서로 맞는지. 템플릿이 없으면 통과</summary>
    private static bool MatchesTemplate(string digits, string? tpl)
    {
        if (string.IsNullOrEmpty(tpl)) return true;
        if (tpl.Length != digits.Length + 1) return false;
        for (int i = 0; i < digits.Length; i++)
            if (char.IsDigit(tpl[i]) && tpl[i] != digits[i]) return false;
        return true;
    }

    /// <summary>수동 학습: 마지막 읽기의 index번째 줄 숫자 부분을 정답으로 학습</summary>
    public (bool ok, string msg) LearnLine(int index, int seconds) =>
        NumMaskAt(index) is { } m ? LearnMask(m, seconds) : (false, "학습할 줄이 없습니다.");

    /// <summary>마지막 읽기의 index번째 줄 숫자 부분 마스크 (읽을 때마다 새 배열이라 보관해도 안전)</summary>
    public bool[,]? NumMaskAt(int index) => index >= 0 && index < _lastNumMasks.Count ? _lastNumMasks[index] : null;

    /// <summary>숫자 부분 마스크를 정답 "N초"로 학습</summary>
    public (bool ok, string msg) LearnMask(bool[,] num, int seconds) => _digits.Learn(num, seconds + "초");

    private void SetPreview(Bitmap img)
    {
        Preview?.Dispose();
        Preview = img;
    }

    // 화면 전체 탐색에서 확인한 숫자 칸 모양 → "N초" 여부 (게임 화면의 고정 글자를 매번 다시 OCR하지 않도록)
    private readonly Dictionary<string, bool> _scanCache = new();

    /// <summary>탐색 후보 줄의 마지막 단어가 "N초"인지: 숫자 템플릿 → PaddleOCR 인식 전용</summary>
    private async Task<bool> IsCooldownNumberAsync(bool[,] mask, Bitmap shot, MaskLine p)
    {
        var num = Crop(mask, p.Top, p.Bottom, p.NumL, p.NumR, pad: 4);
        if (_digits.Templates.Count > 0 && SecRx.IsMatch(_digits.Recognize(num))) return true;
        string key = NameKeyOf(num);
        if (_scanCache.TryGetValue(key, out bool hit)) return hit;
        var rect = Rectangle.Intersect(new Rectangle(p.NumL - 3, p.Top - 3, p.NumR - p.NumL + 7, p.Bottom - p.Top + 7),
                                       new Rectangle(0, 0, shot.Width, shot.Height));
        bool ok = false;
        if (rect.Width > 0 && rect.Height > 0)
        {
            using var raw = shot.Clone(rect, PixelFormat.Format32bppArgb);
            string? text = await Task.Run(() => NameOcr.ReadLine(raw));
            ok = text is not null && SecRx.IsMatch(text);
        }
        if (_scanCache.Count >= 5000) _scanCache.Clear();
        _scanCache[key] = ok;
        return ok;
    }

    /// <summary>
    /// 화면 전체 탐색용 줄 찾기 (Windows OCR 없이):
    /// 흰 글자 마스크를 가로로 번지게(이름↔숫자 간격까지) 해서 붙은 덩어리 = 글자 한 줄 후보로 보고,
    /// 높이가 애드온 글자에 맞는 덩어리만 그 범위 안에서 이름/숫자로 나눔.
    /// 가로줄 전체로 나누면 게임 장면의 밝은 픽셀과 같은 높이에서 붙어 줄을 못 찾음 → 덩어리 단위로 처리.
    /// </summary>
    private static List<MaskLine> ScanLines(bool[,] m, float s)
    {
        int h = m.GetLength(0), w = m.GetLength(1);
        int rx = Math.Max(4, (int)Math.Round(12 * s));   // 가로 번짐 반경 (이름↔숫자 간격 약 14px을 잇는 정도)
        int ry = Math.Max(1, (int)Math.Round(1 * s));    // 세로 번짐 (자모 위아래 조각 연결)
        int minH = Math.Max(5, (int)Math.Round(8 * s)), maxH = (int)Math.Round(22 * s);

        // 가로 번짐: 행마다 누적합으로 ±rx 안에 흰 픽셀이 있으면 true
        var dil = new bool[h, w];
        var pre = new int[w + 1];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) pre[x + 1] = pre[x] + (m[y, x] ? 1 : 0);
            for (int x = 0; x < w; x++)
                dil[y, x] = pre[Math.Min(w, x + rx + 1)] - pre[Math.Max(0, x - rx)] > 0;
        }
        // 세로 번짐
        if (ry > 0)
        {
            var d2 = new bool[h, w];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    if (dil[y, x]) for (int k = Math.Max(0, y - ry); k <= Math.Min(h - 1, y + ry); k++) d2[k, x] = true;
            dil = d2;
        }

        // 붙은 덩어리(4방향) → 경계 상자. 높이가 너무 큰 덩어리(장면/그림)는 중간에 버림
        var seen = new bool[h, w];
        var boxes = new List<Rectangle>();
        var stack = new Stack<(int y, int x)>();
        for (int y0 = 0; y0 < h; y0++)
            for (int x0 = 0; x0 < w; x0++)
            {
                if (!dil[y0, x0] || seen[y0, x0]) continue;
                int t = y0, b = y0, l = x0, r = x0; bool tooBig = false;
                stack.Push((y0, x0)); seen[y0, x0] = true;
                while (stack.Count > 0)
                {
                    var (y, x) = stack.Pop();
                    if (y < t) t = y; if (y > b) b = y; if (x < l) l = x; if (x > r) r = x;
                    if (b - t > maxH * 3) tooBig = true;
                    if (y > 0 && dil[y - 1, x] && !seen[y - 1, x]) { seen[y - 1, x] = true; stack.Push((y - 1, x)); }
                    if (y < h - 1 && dil[y + 1, x] && !seen[y + 1, x]) { seen[y + 1, x] = true; stack.Push((y + 1, x)); }
                    if (x > 0 && dil[y, x - 1] && !seen[y, x - 1]) { seen[y, x - 1] = true; stack.Push((y, x - 1)); }
                    if (x < w - 1 && dil[y, x + 1] && !seen[y, x + 1]) { seen[y, x + 1] = true; stack.Push((y, x + 1)); }
                }
                if (!tooBig) boxes.Add(Rectangle.FromLTRB(l, t, r + 1, b + 1));
            }

        // 덩어리 안에서 기존 줄 나누기(이름 단어들 + 마지막 숫자 단어)로 줄 정보 생성
        var result = new List<MaskLine>();
        foreach (var bx in boxes)
        {
            // 번짐을 뺀 실제 글자 높이
            int t = bx.Bottom, b = bx.Top;
            for (int y = bx.Top; y < bx.Bottom; y++)
                for (int x = bx.Left; x < bx.Right; x++)
                    if (m[y, x]) { t = Math.Min(t, y); b = Math.Max(b, y); break; }
            int lh = b - t + 1;
            if (lh < minH || lh > maxH) continue;
            var sub = Crop(m, t, b, bx.Left, bx.Right - 1, pad: 0);
            foreach (var p in ParseLines(sub, s))
            {
                int numW = p.NumR - p.NumL + 1, nameW = p.NameR - p.NameL + 1, ph = p.Bottom - p.Top + 1;
                if (numW < ph || numW > ph * 6 || nameW < ph || nameW > ph * 14) continue;
                result.Add(new MaskLine(t + p.Top, t + p.Bottom, bx.Left + p.NameL, bx.Left + p.NameR, bx.Left + p.NumL, bx.Left + p.NumR));
            }
        }
        return result;
    }

    // ---------------- 픽셀 줄/단어 분리

    private sealed record MaskLine(int Top, int Bottom, int NameL, int NameR, int NumL, int NumR);

    /// <summary>
    /// 흰 글자 마스크를 줄 → 단어로 분리. 한 줄 = [이름 단어들] [숫자+초]. 단어 간격(공백)은 글자 간격보다 넓음.
    /// </summary>
    private static List<MaskLine> ParseLines(bool[,] m, float s)
    {
        int h = m.GetLength(0), w = m.GetLength(1);
        int rowGapTol = Math.Max(1, (int)Math.Round(2 * s));   // 글자 안 세로 빈 줄 허용
        int minHeight = Math.Max(5, (int)Math.Round(8 * s));    // 줄 최소 높이 (잡티 제외)
        // 단어(이름 ↔ 숫자) 구분 공백 폭. 이름과 숫자 사이 공백은 약 14px, 숫자 '1'끼리 사이는 5px까지 벌어짐
        int wordGap = Math.Max(6, (int)Math.Round(9 * s));

        var rowHas = new bool[h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w && !rowHas[y]; x++) rowHas[y] = m[y, x];

        var result = new List<MaskLine>();
        int yy = 0;
        while (yy < h)
        {
            if (!rowHas[yy]) { yy++; continue; }
            int top = yy, bottom = yy, gap = 0;
            for (yy++; yy < h; yy++)
            {
                if (rowHas[yy]) { bottom = yy; gap = 0; }
                else if (++gap > rowGapTol) break;
            }
            if (bottom - top + 1 < minHeight) continue;

            // 열 단위 글자 덩어리 (픽셀 3개 미만 잡티 제외)
            var runs = new List<(int l, int r)>();
            int start = -1, cnt = 0;
            for (int x = 0; x <= w; x++)
            {
                int c = 0;
                if (x < w) for (int y = top; y <= bottom; y++) if (m[y, x]) c++;
                if (c > 0) { if (start < 0) { start = x; cnt = 0; } cnt += c; }
                else if (start >= 0) { if (cnt >= 3) runs.Add((start, x - 1)); start = -1; }
            }
            // 단어로 묶기
            var words = new List<(int l, int r)>();
            foreach (var run in runs)
            {
                if (words.Count > 0 && run.l - words[^1].r - 1 < wordGap) words[^1] = (words[^1].l, run.r);
                else words.Add(run);
            }
            if (words.Count < 2) continue;
            result.Add(new MaskLine(top, bottom, words[0].l, words[^2].r, words[^1].l, words[^1].r));
        }
        return result;
    }

    private static bool[,] Crop(bool[,] m, int top, int bottom, int left, int right, int pad)
    {
        int ch = bottom - top + 1 + 2 * pad, cw = right - left + 1 + 2 * pad;
        var c = new bool[ch, cw];
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                c[y - top + pad, x - left + pad] = m[y, x];
        return c;
    }

    // ---------------- 이름 모양 지문

    /// <summary>"폭x높이:16진 비트" (글자 영역에 딱 맞게 자른 마스크)</summary>
    private static string NameKeyOf(bool[,] m)
    {
        int h = m.GetLength(0), w = m.GetLength(1);
        var sb = new StringBuilder($"{w}x{h}:");
        int acc = 0, n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                acc = acc << 1 | (m[y, x] ? 1 : 0);
                if (++n == 4) { sb.Append("0123456789abcdef"[acc]); acc = 0; n = 0; }
            }
        if (n > 0) sb.Append("0123456789abcdef"[acc << (4 - n)]);
        return sb.ToString();
    }

    private static bool[,]? ParseKey(string key)
    {
        int colon = key.IndexOf(':'), xPos = key.IndexOf('x');
        if (colon < 0 || xPos < 0 || xPos > colon) return null;
        if (!int.TryParse(key[..xPos], out int w) || !int.TryParse(key[(xPos + 1)..colon], out int h)) return null;
        var m = new bool[h, w];
        string hex = key[(colon + 1)..];
        for (int i = 0; i < w * h; i++)
        {
            int d = i / 4;
            if (d >= hex.Length) break;
            int v = Convert.ToInt32(hex[d].ToString(), 16);
            m[i / w, i % w] = (v >> (3 - i % 4) & 1) == 1;
        }
        return m;
    }

    /// <summary>두 이름 지문의 유사도 (±1px 이동 중 최대 IoU, 크기 차 2px 초과면 0)</summary>
    public static double NameSimilarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (ParseKey(a) is not { } ma || ParseKey(b) is not { } mb) return 0;
        int ha = ma.GetLength(0), wa = ma.GetLength(1), hb = mb.GetLength(0), wb = mb.GetLength(1);
        if (Math.Abs(ha - hb) > 2 || Math.Abs(wa - wb) > 2) return 0;
        double best = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int inter = 0, uni = 0;
                int H = Math.Max(ha, hb + dy) + 1, W = Math.Max(wa, wb + dx) + 1;
                for (int y = Math.Min(0, dy); y < H; y++)
                    for (int x = Math.Min(0, dx); x < W; x++)
                    {
                        bool pa = y >= 0 && y < ha && x >= 0 && x < wa && ma[y, x];
                        int yb = y - dy, xb = x - dx;
                        bool pb = yb >= 0 && yb < hb && xb >= 0 && xb < wb && mb[yb, xb];
                        if (pa && pb) inter++;
                        if (pa || pb) uni++;
                    }
                if (uni > 0) best = Math.Max(best, (double)inter / uni);
            }
        return best;
    }

    // ---------------- 공통

    private const int Pad = 16;

    /// <summary>기준 해상도(세로 720)에서 글자 높이 약 12px → OCR에 약 24px로 넣는 정수 배율</summary>
    private static int OcrScale(float s) => Math.Clamp((int)Math.Round(2.0 / s), 1, 4);

    private static RectangleF Unscale(RectangleF r, int scale) =>
        new((r.X - Pad) / scale, (r.Y - Pad) / scale, r.Width / scale, r.Height / scale);

    private static Rectangle ToScreen(RectangleF roi, Rectangle canvas) => Rectangle.Round(new RectangleF(
        canvas.X + roi.X * canvas.Width, canvas.Y + roi.Y * canvas.Height, roi.Width * canvas.Width, roi.Height * canvas.Height));

    private static string Clean(string s) => s.Replace(" ", " ").Trim();

    /// <summary>이름 비교용: 공백 제거</summary>
    public static string NormalizeName(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>흰 글자(밝고 채도 낮음)만 true. 반투명 창 뒤 배경은 어둡거나 색이 있어 제외됨</summary>
    private static bool[,] KeyWhite(Bitmap src)
    {
        var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[data.Stride * src.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        int stride = data.Stride;
        src.UnlockBits(data);

        var mask = new bool[src.Height, src.Width];
        for (int y = 0; y < src.Height; y++)
        for (int x = 0; x < src.Width; x++)
        {
            int o = y * stride + x * 4;
            int b = px[o], g = px[o + 1], r = px[o + 2];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            // HSL 밝기 ≥ 0.6, 채도 ≤ 0.25
            int sum = max + min;
            if (sum < 306) continue;
            int diff = max - min;
            float sat = sum >= 510 ? 0 : diff / (float)(sum > 255 ? 510 - sum : sum);
            mask[y, x] = sat <= 0.25f;
        }
        return mask;
    }
}
