using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RegionOcr;

/// <summary>
/// 게임 등 고정 픽셀 폰트용 인식기.
/// 글자색(hue) 키잉으로 배경을 제거 → 열 단위로 글자 분리 → 학습된 템플릿과 비교.
/// </summary>
public sealed class GlyphMatcher
{
    public sealed class Template
    {
        public char Ch { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public string Bits { get; set; } = ""; // 행 우선 '0'/'1'
    }

    private sealed record Glyph(int X, int Y, int W, int H, bool[,] Px) // Px[y,x]
    {
        private float[]? _norm;
        /// <summary>정규화 격자 (글자당 한 번만 계산)</summary>
        public float[] Norm => _norm ??= Normalize((x, y) => Px[y, x], W, H);
    }

    private const double MatchThreshold = 0.60; // 1순위 점수가 이 미만이면 '?'
    private const double MinMargin = 0.05;      // 1순위와 2순위(다른 글자) 점수 차가 이 미만이면 '?'
    private const int EdgeMargin = 3;           // 영역 좌우 최소 여백(px) = 글자 간격 이상
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "templates.json");
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public List<Template> Templates { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Templates = JsonSerializer.Deserialize<List<Template>>(File.ReadAllText(FilePath), Opt) ?? new();
            _normCache.Clear();
        }
        catch { Templates = new(); }
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(Templates, Opt));

    /// <summary>templates.json 수정 시각 (외부에서 학습 파일이 바뀌면 다시 읽기 위함)</summary>
    public static DateTime FileStamp => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

    // ---------------- 색상 키잉

    /// <summary>글자색과 색상(hue)이 가까운 픽셀만 true (배경 제거)</summary>
    public static bool[,] Key(Bitmap src, Color keyColor, float hueTol)
    {
        float keyHue = keyColor.GetHue();
        // 밝은 글자색(크림/노랑 등)은 비슷한 hue의 어두운 배경(갈색)과 구분되도록 밝기 하한을 올림
        float minL = Math.Max(0.12f, keyColor.GetBrightness() - 0.35f);
        byte[] px = ReadPixels(src, out int stride);
        var mask = new bool[src.Height, src.Width];
        for (int y = 0; y < src.Height; y++)
        for (int x = 0; x < src.Width; x++)
        {
            int o = y * stride + x * 4;
            var c = Color.FromArgb(px[o + 2], px[o + 1], px[o]);
            float d = Math.Abs(c.GetHue() - keyHue);
            d = Math.Min(d, 360 - d);
            mask[y, x] = d <= hueTol && c.GetSaturation() >= 0.2f && c.GetBrightness() >= minL;
        }
        return mask;
    }

    /// <summary>마스크를 흑백 이미지로 (미리보기/OCR 대체 입력용)</summary>
    /// <param name="fg">글자색 (기본 검정 = OCR 입력용)</param>
    /// <param name="bg">배경색 (기본 흰색)</param>
    public static Bitmap ToBitmap(bool[,] mask, int scale = 4, int pad = 8, Color? fg = null, Color? bg = null)
    {
        int h = mask.GetLength(0), w = mask.GetLength(1);
        var bmp = new Bitmap(w * scale + 2 * pad, h * scale + 2 * pad, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(bg ?? Color.White);
        using var brush = new SolidBrush(fg ?? Color.Black);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (mask[y, x]) g.FillRectangle(brush, pad + x * scale, pad + y * scale, scale, scale);
        return bmp;
    }

    // ---------------- 글자 분리

    private static List<Glyph> Segment(bool[,] mask)
    {
        int h = mask.GetLength(0), w = mask.GetLength(1);
        var runs = new List<(int x0, int x1)>();
        int start = -1;
        for (int x = 0; x <= w; x++)
        {
            bool any = false;
            if (x < w) for (int y = 0; y < h && !any; y++) any = mask[y, x];
            if (any && start < 0) start = x;
            if (!any && start >= 0) { runs.Add((start, x - 1)); start = -1; }
        }

        var glyphs = new List<Glyph>();
        foreach (var (x0, x1) in runs)
        {
            int y0 = h, y1 = -1, cnt = 0;
            for (int y = 0; y < h; y++)
            for (int x = x0; x <= x1; x++)
                if (mask[y, x]) { y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); cnt++; }
            if (cnt < 4) continue; // 잡티
            int gw = x1 - x0 + 1, gh = y1 - y0 + 1;
            var p = new bool[gh, gw];
            for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
                p[y, x] = mask[y0 + y, x0 + x];
            glyphs.Add(new Glyph(x0, y0, gw, gh, p));
        }

        // 최대 글자 높이의 40% 미만인 조각(점, 잡티)은 제외
        int maxH = glyphs.Count == 0 ? 0 : glyphs.Max(g => g.H);
        return glyphs.Where(g => g.H >= maxH * 0.4).ToList();
    }

    // ---------------- 인식 / 학습

    /// <summary>템플릿 매칭으로 문자열 인식 (일치 안 되는 글자는 '?')</summary>
    public string Recognize(bool[,] mask)
    {
        var sb = new StringBuilder();
        int width = mask.GetLength(1);
        foreach (var g in Segment(mask))
        {
            // 영역 좌우 경계에서 EdgeMargin 이내의 글자는 인식 불가 처리.
            // 경계에 걸려 잘린 글자뿐 아니라, 영역 밖에 완전히 빠진 앞자리(자릿수 초과)가 있을 가능성도 차단
            if (g.X < EdgeMargin || g.X + g.W > width - EdgeMargin) { sb.Append('?'); continue; }

            // 글자별 최고 점수 → 1순위가 기준 이상이고 2순위(다른 글자)와 충분히 차이 날 때만 인정
            var ranked = Templates.GroupBy(t => t.Ch)
                .Select(grp => (ch: grp.Key, s: grp.Max(t => Score(g, t))))
                .OrderByDescending(v => v.s).ToList();
            double first = ranked.Count > 0 ? ranked[0].s : 0, second = ranked.Count > 1 ? ranked[1].s : 0;
            sb.Append(first >= MatchThreshold && first - second >= MinMargin ? ranked[0].ch : '?');
        }
        return sb.ToString();
    }

    /// <summary>진단용: 글자별 1순위/2순위 후보와 점수</summary>
    public string Explain(bool[,] mask)
    {
        var parts = new List<string>();
        foreach (var g in Segment(mask))
        {
            var top = Templates.GroupBy(t => t.Ch)
                .Select(grp => (ch: grp.Key, s: grp.Max(t => Score(g, t))))
                .OrderByDescending(v => v.s).Take(2).ToList();
            parts.Add(string.Join("/", top.Select(v => $"{v.ch}:{v.s:F2}")));
        }
        return string.Join("  ", parts);
    }

    /// <summary>현재 마스크의 글자들을 정답 문자열로 학습. 반환: (성공 여부, 메시지)</summary>
    public (bool ok, string msg) Learn(bool[,] mask, string answer)
    {
        var chars = answer.Where(c => !char.IsWhiteSpace(c)).ToArray();
        var glyphs = Segment(mask);
        if (glyphs.Count != chars.Length)
            return (false, $"글자 수 불일치: 화면에서 {glyphs.Count}개를 찾았는데 입력은 {chars.Length}개입니다.");

        int added = 0;
        for (int i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            // 같은 글자로 거의 동일한 템플릿이 이미 있으면 추가하지 않음
            if (Templates.Any(t => t.Ch == chars[i] && Score(g, t) >= 0.97)) continue;
            Templates.Add(new Template { Ch = chars[i], W = g.W, H = g.H, Bits = Encode(g.Px) });
            added++;
        }
        Save();
        var have = string.Concat(Templates.Select(t => t.Ch).Distinct().OrderBy(c => c));
        return (true, $"{added}개 템플릿 추가 (학습된 글자: {have})");
    }

    // 크기 정규화 격자 (게임이 확대/축소되어 그려져도 같은 템플릿으로 비교)
    private const int NW = 12, NH = 16;
    private readonly Dictionary<Template, float[]> _normCache = new();

    /// <summary>
    /// 유사도: 글자와 템플릿을 같은 크기(12x16) 농도 지도로 정규화한 뒤 Σmin/Σmax (±1칸 이동 중 최대).
    /// 가로세로 비율이 30% 넘게 다르면 0 ('1'과 다른 숫자 구분).
    /// </summary>
    private double Score(Glyph g, Template t)
    {
        double ag = (double)g.W / g.H, at = (double)t.W / t.H;
        if (Math.Abs(ag - at) / at > 0.45) return 0;

        float[] a = g.Norm;
        if (!_normCache.TryGetValue(t, out var b))
            _normCache[t] = b = Normalize((x, y) => t.Bits[y * t.W + x] == '1', t.W, t.H);

        double best = 0;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            double mn = 0, mx = 0;
            for (int y = 0; y < NH; y++)
            for (int x = 0; x < NW; x++)
            {
                float va = a[y * NW + x];
                int tx = x + dx, ty = y + dy;
                float vb = tx >= 0 && tx < NW && ty >= 0 && ty < NH ? b[ty * NW + tx] : 0;
                mn += Math.Min(va, vb);
                mx += Math.Max(va, vb);
            }
            if (mx > 0) best = Math.Max(best, mn / mx);
        }
        return best;
    }

    /// <summary>w x h 이진 글자를 NW x NH 농도(0~1) 격자로 변환 (셀마다 4x4 샘플 평균)</summary>
    private static float[] Normalize(Func<int, int, bool> px, int w, int h)
    {
        var r = new float[NW * NH];
        for (int cy = 0; cy < NH; cy++)
        for (int cx = 0; cx < NW; cx++)
        {
            int on = 0;
            for (int sy = 0; sy < 4; sy++)
            for (int sx = 0; sx < 4; sx++)
            {
                int x = (int)((cx + (sx + 0.5) / 4) * w / NW);
                int y = (int)((cy + (sy + 0.5) / 4) * h / NH);
                if (px(Math.Min(x, w - 1), Math.Min(y, h - 1))) on++;
            }
            r[cy * NW + cx] = on / 16f;
        }
        return r;
    }

    private static string Encode(bool[,] p)
    {
        var sb = new StringBuilder(p.Length);
        for (int y = 0; y < p.GetLength(0); y++)
        for (int x = 0; x < p.GetLength(1); x++)
            sb.Append(p[y, x] ? '1' : '0');
        return sb.ToString();
    }

    private static byte[] ReadPixels(Bitmap bmp, out int stride)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            var bytes = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { bmp.UnlockBits(data); }
    }
}
