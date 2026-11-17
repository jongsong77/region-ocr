using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RegionOcr;

/// <summary>OCR 대상 영역 하나 (화면 물리 픽셀 좌표)</summary>
public sealed class RegionItem
{
    [DisplayName("이름")]
    public string Name { get; set; } = "";

    [DisplayName("숫자만")]
    public bool NumericOnly { get; set; } = true;

    /// <summary>용도: "", "HP", "MP", "EXP"</summary>
    public string Role { get; set; } = "";

    /// <summary>이 영역의 읽기 주기(ms). 0이면 기본 주기마다 읽음</summary>
    public int IntervalMs { get; set; }

    [JsonIgnore, Browsable(false)]
    public DateTime LastRead { get; set; } = DateTime.MinValue;

    [Browsable(false)] public int X { get; set; }
    [Browsable(false)] public int Y { get; set; }
    [Browsable(false)] public int W { get; set; }
    [Browsable(false)] public int H { get; set; }

    // 글자색(ARGB). 지정 시 색상 키잉 + 템플릿 매칭, 없으면 Windows OCR
    [Browsable(false)]
    public int? KeyColor { get; set; }

    [JsonIgnore, ReadOnly(true), DisplayName("방식")]
    public string Mode => KeyColor is int c ? $"글자색 #{c & 0xFFFFFF:X6}" : "OCR";

    [JsonIgnore, ReadOnly(true), DisplayName("값")]
    public string Value { get; set; } = "";

    [JsonIgnore, ReadOnly(true), DisplayName("인식 원문")]
    public string Raw { get; set; } = "";

    [JsonIgnore, ReadOnly(true), DisplayName("읽은 시각")]
    public string Time { get; set; } = "";

    [JsonIgnore, ReadOnly(true), DisplayName("영역(x,y,w,h)")]
    public string RectText => $"{X},{Y},{W},{H}";

    // 마지막으로 OCR에 넣은 전처리 이미지 (미리보기용)
    [JsonIgnore, Browsable(false)]
    public Bitmap? Preview { get; set; }

    // 마지막 원본 캡처 (글자색 클릭용) / 마지막 키잉 마스크 (학습용)
    [JsonIgnore, Browsable(false)]
    public Bitmap? Shot { get; set; }

    [JsonIgnore, Browsable(false)]
    public bool[,]? Mask { get; set; }

    [JsonIgnore, Browsable(false)]
    public Rectangle Rect
    {
        get => new(X, Y, W, H);
        set { X = value.X; Y = value.Y; W = value.Width; H = value.Height; }
    }
}

/// <summary>스킬 쿨타임 자동 입력 규칙: [스킬] 남은 시간이 [초] 이하가 되면 [키] 1회 입력</summary>
public sealed class SkillRule
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";     // 표시 이름 (사용자 입력)
    public string OcrName { get; set; } = "";  // 등록 당시 OCR이 읽은 이름 (참고용, 픽셀 글꼴이라 매번 다를 수 있음)
    public string NameKey { get; set; } = "";  // 이름 부분 픽셀 모양 지문 (실제 비교 기준)

    [JsonIgnore] public string Shape => NameKey.Length > 0 ? "등록됨" : "없음";
    public int Seconds { get; set; } = 1;      // 0 = 쿨이 끝나 창에서 사라질 때
    public string Key { get; set; } = "";

    [JsonIgnore] public string Status { get; set; } = "";
}

/// <summary>설정 파일(config.json) 저장/로드</summary>
public sealed class AppConfig
{
    public int IntervalSec { get; set; } = 5;   // 구버전 호환용 (IntervalMs 없을 때만 사용)
    public int IntervalMs { get; set; }          // 읽기 주기(ms)
    public bool OnlyOnChange { get; set; } = true;
    public decimal Scale { get; set; } = 2.0m; // OCR 전 확대 배율

    public bool LogEnabled { get; set; }            // CSV 기록 (기본 꺼짐)
    public string StartHotkey { get; set; } = "F9"; // 시작/정지 전역 단축키
    public string ResetHotkey { get; set; } = "F10"; // 사냥 초기화 전역 단축키
    public bool SaveUnknown { get; set; }            // 인식 실패('?') 이미지를 unknown 폴더에 저장

    // MP 자동 키 입력
    public bool MpAutoEnabled { get; set; }
    public int MpThreshold { get; set; } = 60;
    public string MpKey { get; set; } = "F";
    public int MpCooldownMs { get; set; } = 1000;

    // 게임 창 기준: 창 제목과, 현재 영역 좌표가 맞춰진 시점의 게임 화면(검은 여백 제외) 위치·크기.
    // 시작 시/창 변경 시 게임 화면을 다시 계산해 위치·배율 변화만큼 영역과 오버레이를 변환
    public string? GameTitle { get; set; }
    public int RefCanvasX { get; set; }
    public int RefCanvasY { get; set; }
    public int RefCanvasW { get; set; }
    public int RefCanvasH { get; set; }

    [JsonIgnore]
    public Rectangle RefCanvas
    {
        get => new(RefCanvasX, RefCanvasY, RefCanvasW, RefCanvasH);
        set { RefCanvasX = value.X; RefCanvasY = value.Y; RefCanvasW = value.Width; RefCanvasH = value.Height; }
    }

    // 오버레이
    public bool OverlayVisible { get; set; } = true;
    public bool OverlayGraphMode { get; set; }               // 오버레이 그래프 모드 (오른쪽 클릭으로 전환)
    public List<string> OverlayHidden { get; set; } = new(); // 오버레이에서 숨길 항목 키
    public int OverlayX { get; set; } = 100;
    public int OverlayY { get; set; } = 100;
    public List<RegionItem> Regions { get; set; } = new();

    // 스킬 쿨타임(애드온 창) 자동 입력
    public bool SkillAutoEnabled { get; set; }
    public List<SkillRule> SkillRules { get; set; } = new();

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    /// <summary>설정 형식 버전 (0 = 버전 도입 전 파일)</summary>
    public int ConfigVersion { get; set; }
    private const int CurrentVersion = 2;

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Opt) ?? new AppConfig();
                if (cfg.Migrate()) cfg.Save();
                return cfg;
            }
        }
        catch
        {
            // 설정 파일이 깨진 경우 기본값으로 시작
        }
        return new AppConfig();
    }

    /// <summary>구버전 설정 보정. 반환: 변경 여부</summary>
    private bool Migrate()
    {
        if (ConfigVersion >= CurrentVersion) return false;
        if (ConfigVersion < 2)
        {
            // v2: EXP 숫자는 오른쪽 정렬이라 자릿수가 늘면 왼쪽으로 자람.
            // 11자리(100억 이상)에서 맨 앞자리가 영역 왼쪽 경계에 붙어 '?' 처리되던 문제 → 왼쪽으로 글자 한 칸 가량 확장
            foreach (var r in Regions.Where(r => r.Role == "EXP"))
            {
                int ext = Math.Max(4, r.H / 2);
                r.X -= ext;
                r.W += ext;
            }
        }
        ConfigVersion = CurrentVersion;
        return true;
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opt));
}
