using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using RapidOcrNet;
using SkiaSharp;

namespace RegionOcr;

/// <summary>
/// 애드온 창 한글 OCR (PaddleOCR PP-OCRv5 한국어 인식 모델, ONNX).
///  - 스킬 이름(표시용): 검출+인식. 비교 시험에서 Windows OCR(정답 0%)보다 정확(73%) → [스킬 설정] 목록 이름에 사용.
///    스킬 구분(규칙 매칭)은 이름 모양 지문으로 하므로 이름이 틀려도 동작에는 영향 없음.
///  - 남은 초: 인식 전용(검출 생략, 1줄 약 35ms). 게임 창 크기가 바뀌어 글자가 흐려지면 숫자 템플릿이 거의 못 읽음(10%) →
///    템플릿이 못 읽은 줄만 이것으로 읽음. 비교 시험 464줄에서 정답 100%, 오답 0.
/// 모델 파일이 없거나 초기화에 실패하면 null을 돌려줌 (이름은 Windows OCR로 대신, 초는 판정 보류).
/// </summary>
public static class NameOcr
{
    private static readonly object Gate = new();
    private static RapidOcr? _ocr;
    private static TextRecognizer? _rec;
    private static bool _tried, _recTried;
    private static readonly RapidOcrOptions Options = RapidOcrOptions.Default with { DoAngle = false };

    private static string ModelDir => Path.Combine(AppContext.BaseDirectory, "models", "v5");
    private static string RecModel => Path.Combine(ModelDir, "korean_PP-OCRv5_rec_mobile.onnx");
    private static string RecDict => Path.Combine(ModelDir, "ppocrv5_korean_dict.txt");

    /// <summary>초기화 실패 사유 (로그용, 성공이면 null)</summary>
    public static string? Error { get; private set; }

    private static RapidOcr? Engine()
    {
        if (_tried) return _ocr;
        _tried = true;
        try
        {
            var ocr = new RapidOcr();
            ocr.InitModels(
                Path.Combine(ModelDir, "ch_PP-OCRv5_mobile_det.onnx"),
                Path.Combine(ModelDir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                RecModel, RecDict);
            _ocr = ocr;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            AppLog.Write($"한글 이름 OCR(PaddleOCR) 초기화 실패 → Windows OCR 사용: {ex.Message}");
        }
        return _ocr;
    }

    private static TextRecognizer? Recognizer()
    {
        if (_recTried) return _rec;
        _recTried = true;
        try
        {
            var rec = new TextRecognizer();
            rec.InitModel(RecModel, RecDict, 1);
            _rec = rec;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            AppLog.Write($"남은 초 OCR(PaddleOCR) 초기화 실패 → 숫자 템플릿만 사용: {ex.Message}");
        }
        return _rec;
    }

    /// <summary>원본 색상 이름 영역을 읽어 한글·영숫자만 남긴 문자열. 엔진을 못 쓰면 null</summary>
    public static string? Read(Bitmap rawName)
    {
        lock (Gate)
        {
            var ocr = Engine();
            if (ocr is null) return null;
            using var sk = ToSkia3x(rawName);
            string text = ocr.Detect(sk, Options).StrRes;
            return new string(text.Where(c => (c >= '가' && c <= '힣') || char.IsLetterOrDigit(c)).ToArray());
        }
    }

    /// <summary>한 줄 글자 영역을 인식 전용으로 읽은 문자열(공백 제거). 엔진을 못 쓰면 null</summary>
    public static string? ReadLine(Bitmap raw)
    {
        lock (Gate)
        {
            var rec = Recognizer();
            if (rec is null) return null;
            using var sk = ToSkia3x(raw);
            return new string(string.Concat(rec.GetTextLine(sk, CancellationToken.None).Chars ?? []).Where(c => !char.IsWhiteSpace(c)).ToArray());
        }
    }

    /// <summary>
    /// 원본 색상 "N초" 영역에서 숫자 부분 문자열(예: "81").
    /// 끝의 한글('초')은 떼고, 숫자와 헷갈리는 영문만 숫자로 바꿈(B→8, O→0, I·l·|→1, S→5, Z→2).
    /// 그 밖의 글자가 섞이면 null — 예전에는 숫자만 골라 써서 "81초"를 "B1초"로 읽으면 1초로 오판했음.
    /// </summary>
    public static string? ReadSecondsText(Bitmap rawNumber)
    {
        lock (Gate)
        {
            var rec = Recognizer();
            if (rec is null) return null;
            using var sk = ToSkia3x(rawNumber);
            string text = new(string.Concat(rec.GetTextLine(sk, CancellationToken.None).Chars ?? []).Where(c => !char.IsWhiteSpace(c)).ToArray());
            int end = text.Length;
            while (end > 0 && text[end - 1] is >= '가' and <= '힣' && text.Length - end < 2) end--;
            var sb = new System.Text.StringBuilder();
            foreach (char c in text[..end])
            {
                char d = c switch { 'B' => '8', 'O' or 'o' => '0', 'I' or 'l' or '|' => '1', 'S' => '5', 'Z' => '2', _ => c };
                if (d is < '0' or > '9') return null;
                sb.Append(d);
            }
            return sb.Length is >= 1 and <= 4 ? sb.ToString() : null;
        }
    }

    // 게임 글자는 11~14px → 3배 확대해야 인식 모델 입력 크기에 맞음
    private static SKBitmap ToSkia3x(Bitmap src)
    {
        using var up = new Bitmap(src.Width * 3, src.Height * 3, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(up))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, 0, 0, up.Width, up.Height);
        }
        using var ms = new MemoryStream();
        up.Save(ms, ImageFormat.Png);
        return SKBitmap.Decode(ms.ToArray());
    }
}
