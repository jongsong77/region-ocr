using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace RegionOcr;

/// <summary>화면 캡처 + 전처리 + Windows 내장 OCR</summary>
public sealed class OcrService
{
    private readonly OcrEngine _engine;
    public string LanguageTag { get; }

    public OcrService()
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Language("ko"))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? throw new InvalidOperationException("사용 가능한 Windows OCR 언어가 없습니다.");
        LanguageTag = _engine.RecognizerLanguage.LanguageTag;
    }

    /// <summary>화면 영역 캡처 (물리 픽셀 좌표)</summary>
    public static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        return bmp;
    }

    /// <summary>확대 → 흑백 → 어두운 배경이면 반전 → 흰 여백 추가</summary>
    /// <param name="scale">확대 배율 (화면 글씨 9~10pt 기준 2배 내외가 가장 정확, 과확대 시 오인식 증가)</param>
    public static Bitmap Preprocess(Bitmap src, float scale = 2f)
    {
        const int pad = 16;
        int max = (int)OcrEngine.MaxImageDimension - 2 * pad;
        scale = Math.Min(scale, max / (float)Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)(src.Width * scale));
        int h = Math.Max(1, (int)(src.Height * scale));

        using var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, 0, 0, w, h);
        }

        byte[] px = ReadPixels(scaled);
        var gray = new byte[w * h];
        long sum = 0;
        for (int i = 0; i < gray.Length; i++)
        {
            int o = i * 4; // BGRA
            gray[i] = (byte)((px[o + 2] * 299 + px[o + 1] * 587 + px[o] * 114) / 1000);
            sum += gray[i];
        }
        bool invert = sum / gray.Length < 128; // 어두운 배경 + 밝은 글자 → 반전

        int ow = w + 2 * pad, oh = h + 2 * pad;
        var outPx = new byte[ow * oh * 4];
        Array.Fill(outPx, (byte)255); // 흰 배경
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            byte v = gray[y * w + x];
            if (invert) v = (byte)(255 - v);
            int o = ((y + pad) * ow + (x + pad)) * 4;
            outPx[o] = outPx[o + 1] = outPx[o + 2] = v;
            outPx[o + 3] = 255;
        }

        var result = new Bitmap(ow, oh, PixelFormat.Format32bppArgb);
        WritePixels(result, outPx);
        return result;
    }

    /// <summary>OCR 수행 후 줄 단위 텍스트 반환</summary>
    public async Task<string> RecognizeAsync(Bitmap bmp)
    {
        var writer = new DataWriter();
        writer.WriteBytes(ReadPixels(bmp));
        using var sb = SoftwareBitmap.CreateCopyFromBuffer(
            writer.DetachBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(sb);
        return string.Join("\n", result.Lines.Select(l => l.Text));
    }

    /// <summary>OCR 수행 후 줄별 텍스트와 위치(입력 이미지 좌표) 반환</summary>
    public async Task<List<(string text, RectangleF rect)>> RecognizeLinesAsync(Bitmap bmp)
    {
        var writer = new DataWriter();
        writer.WriteBytes(ReadPixels(bmp));
        using var sb = SoftwareBitmap.CreateCopyFromBuffer(
            writer.DetachBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(sb);
        var lines = new List<(string, RectangleF)>();
        foreach (var l in result.Lines)
        {
            var rs = l.Words.Select(w => w.BoundingRect).ToList();
            float x0 = (float)rs.Min(r => r.X), y0 = (float)rs.Min(r => r.Y);
            float x1 = (float)rs.Max(r => r.X + r.Width), y1 = (float)rs.Max(r => r.Y + r.Height);
            lines.Add((l.Text, RectangleF.FromLTRB(x0, y0, x1, y1)));
        }
        return lines;
    }

    // 부호는 앞이 숫자/영문이 아닐 때만 인정(날짜 2026-11-11 의 '-' 제외), 영문 바로 뒤 숫자는 제외(MB→M3 오인식 등)
    private static readonly Regex NumRx = new(@"(?:(?<![0-9A-Za-z])[-+])?(?<![A-Za-z])\d+(?:[.,]\d+)*", RegexOptions.Compiled);
    private static readonly Regex ThousandsRx = new(@"^[-+]?\d{1,3}(,\d{3})+(\.\d+)?$", RegexOptions.Compiled);
    // 천 단위 콤마가 공백으로 인식된 경우(1 234) → 콤마로 복원
    private static readonly Regex SplitThousandsRx = new(@"(?<=(?:^|[^\d.,])\d{1,3}(?: \d{3})*) (?=\d{3}(?!\d))", RegexOptions.Compiled);

    /// <summary>
    /// 숫자만 추출. 숫자가 포함된 토큰 안의 O/o→0, I/l/|→1 오인식을 보정하고
    /// 천 단위 콤마는 제거. 숫자가 여러 개면 공백으로 구분.
    /// </summary>
    public static string ExtractNumbers(string raw)
    {
        var values = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var tokens = line.Split((char[])[' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries)
                             .Select(t => t.Any(char.IsDigit) ? FixConfusables(t) : t);
            string text = SplitThousandsRx.Replace(string.Join(" ", tokens), ",");
            foreach (Match m in NumRx.Matches(text))
                values.Add(ThousandsRx.IsMatch(m.Value) ? m.Value.Replace(",", "") : m.Value);
        }
        return string.Join(" ", values);
    }

    private static string FixConfusables(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (char c in s)
            b.Append(c switch
            {
                'O' or 'o' => '0',
                'I' or 'l' or '|' => '1',
                _ => c
            });
        return b.ToString();
    }

    private static byte[] ReadPixels(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { bmp.UnlockBits(data); }
    }

    private static void WritePixels(Bitmap bmp, byte[] bytes)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(bytes, 0, data.Scan0, bytes.Length); }
        finally { bmp.UnlockBits(data); }
    }
}
