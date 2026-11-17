using System.ComponentModel;
using System.Text;

namespace RegionOcr;

public sealed class MainForm : Form
{
    private readonly AppConfig _cfg = AppConfig.Load();
    private readonly BindingList<RegionItem> _regions;
    private readonly OcrService? _ocr;
    private readonly System.Windows.Forms.Timer _timer = new();
    private bool _busy;
    private const float HueTolerance = 30f;
    private const int HotkeyId = 1, ResetHotkeyId = 2;
    private readonly Button _btnReset = new();
    private readonly KeyCaptureBox _resetHotkey = new();
    private readonly CheckBox _saveUnknown = new();
    private DateTime _tplStamp;                           // templates.json 수정 시각 (외부 학습 반영용)
    private readonly Dictionary<RegionItem, (string raw, DateTime at)> _lastUnknown = new();
    private static string UnknownDir => Path.Combine(AppContext.BaseDirectory, "unknown");

    // 오버레이 표시 항목 (키, 설정 화면 이름)
    private static readonly (string key, string label)[] OverlayItems =
    [
        ("state", "상태·경과 시간"), ("hpmp", "HP/MP"), ("startExp", "시작 경험치"), ("curExp", "현재 경험치"),
        ("gained", "누적 EXP"), ("perMin", "분당 EXP"), ("perHour", "시간당 EXP"), ("hourly", "시간별 누적"),
        ("mpAuto", "MP 자동 입력 횟수"), ("warn", "인식 불가 경고"),
    ];
    private bool ShowItem(string key) => !_cfg.OverlayHidden.Contains(key);
    private static readonly string[] Roles = ["", "HP", "MP", "EXP"];

    // ---- 기본 화면
    private readonly Button _btnStart = new();
    private readonly Button _btnSettings = new();
    private readonly Button _btnOverlay = new();
    private readonly Label _lblHp = new(), _lblMp = new(), _lblExp = new();
    private readonly Label _lblHunt = new(), _lblPerMin = new(), _lblPerHour = new();
    private readonly CheckBox _mpAuto = new();
    private readonly NumericUpDown _mpThreshold = new();
    private readonly KeyCaptureBox _mpKey = new();
    private readonly Label _autoInfo = new();
    private readonly Label _status = new();

    // ---- 설정 (펼침)
    private readonly Panel _settings = new();
    private readonly DataGridView _grid = new();
    private readonly NumericUpDown _interval = new();
    private readonly NumericUpDown _mpCooldown = new();
    private readonly NumericUpDown _scale = new();
    private readonly KeyCaptureBox _hotkey = new();
    private readonly CheckBox _logEnabled = new();
    private readonly CheckBox _onlyChange = new();
    private readonly PictureBox _preview = new();
    private readonly PictureBox _shotPreview = new();
    private readonly Label _tplInfo = new();
    private readonly GlyphMatcher _matcher = new();
    private bool _pickingColor;

    // ---- 사냥 통계 / 오버레이 / MP 자동 입력 / 게임 창 추적
    private readonly HuntTracker _hunt = new();
    private readonly OverlayForm _overlay = new();
    private GraphForm? _graph;
    private readonly Button _btnGraph = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    private DateTime _lastKeyAt = DateTime.MinValue;
    private int _keyCount;
    private int _mpLowStreak;                 // MP가 기준 미만으로 연속 읽힌 횟수
    private DateTime? _allUnreadableSince;    // 용도 영역이 전부 인식 불가가 된 시각
    private DateTime _lastFindAt = DateTime.MinValue;
    private bool _finding;
    private Rectangle _lastClient;            // 마지막으로 본 게임 창 클라이언트 영역 (변경 감지용)
    private string _lastAutoMsg = "";

    private static string LogDir => Path.Combine(AppContext.BaseDirectory, "logs");
    private const int CompactHeight = 214, SettingsHeight = 700, CompactWidth = 430, SettingsWidth = 760;

    public MainForm()
    {
        Text = "Region OCR";
        Font = new Font("맑은 고딕", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(CompactWidth, CompactHeight);

        _regions = new BindingList<RegionItem>(_cfg.Regions);
        _hunt.Log += AppLog.Write;
        AppLog.Write($"프로그램 시작 (시작/일시정지 {_cfg.StartHotkey}, 중지 {_cfg.ResetHotkey})");
        _matcher.Load();
        BuildUi();
        Theme.Apply(this);       // 디스코드 스타일 다크 테마
        UpdateStartButton();
        UpdateTemplateInfo();

        try { _ocr = new OcrService(); }
        catch { /* OCR 언어팩이 없어도 글자색(템플릿 매칭) 영역은 동작 */ }

        _timer.Tick += async (_, _) => await ReadAllAsync();

        // 오버레이: 저장된 위치에 표시, 이동 시 위치 저장
        _overlay.Location = new Point(_cfg.OverlayX, _cfg.OverlayY);
        _overlay.LocationChanged += (_, _) => { _cfg.OverlayX = _overlay.Left; _cfg.OverlayY = _overlay.Top; };
        _overlay.SetGraphMode(_cfg.OverlayGraphMode);
        _overlay.ModeToggled += (_, _) =>
        {
            _cfg.OverlayGraphMode = _overlay.GraphMode;
            SaveConfig();
            AppLog.Write($"오버레이 모드: {(_overlay.GraphMode ? "그래프" : "텍스트")}");
            UpdateDisplay();
        };
        if (_cfg.OverlayVisible) _overlay.Show();
        UpdateOverlayButton();

        _tplStamp = GlyphMatcher.FileStamp;
        _uiTimer.Tick += async (_, _) =>
        {
            ReloadTemplatesIfChanged();
            UpdateDisplay();
            _graph?.Tick();
            _overlay.KeepOnTop();
            await WatchGameWindowAsync();
        };
        _uiTimer.Start();

        // 시작 시: 단축키 등록, 게임 창에 영역 자동 맞춤
        Shown += async (_, _) =>
        {
            RegisterStartHotkey();
            EnsureOverlayOnScreen();
            UpdateDisplay();
            await FitToGameAsync(quiet: false);
        };
    }

    // =====================================================================
    // 화면 구성
    // =====================================================================

    private void BuildUi()
    {
        var bold11 = new Font("맑은 고딕", 11f, FontStyle.Bold);

        // ---- 1줄: 시작/정지 + 설정
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 2, Padding = new Padding(8, 8, 8, 0) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        _btnStart.Dock = DockStyle.Fill;
        _btnStart.Font = bold11;
        _btnStart.Click += (_, _) => ToggleRunning("버튼");
        _btnSettings.Text = "⚙ 설정";
        _btnSettings.Dock = DockStyle.Fill;
        _btnSettings.Click += (_, _) => ToggleSettings();
        top.Controls.Add(_btnStart, 0, 0);
        top.Controls.Add(_btnSettings, 1, 0);

        // ---- 2~3줄: 현재값 / 사냥 통계
        // 값: 등록된 HP/MP/EXP만 왼쪽부터 표시 / 통계: 왼쪽 경과·누적, 오른쪽 분당·시간당
        var info = new TableLayoutPanel { Dock = DockStyle.Top, Height = 80, ColumnCount = 2, RowCount = 2, Padding = new Padding(8, 6, 10, 0) };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        info.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var values = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        foreach (var l in new[] { _lblHp, _lblMp, _lblExp }) { l.AutoSize = true; l.Font = bold11; l.Margin = new Padding(0, 0, 18, 2); }
        foreach (var l in new[] { _lblHunt, _lblPerMin, _lblPerHour }) { l.AutoSize = true; l.ForeColor = Theme.Muted; }
        _lblHp.ForeColor = Color.FromArgb(0xF2, 0x3F, 0x42);
        _lblMp.ForeColor = Theme.Blue;
        _lblExp.ForeColor = Theme.Green;
        values.Controls.AddRange([_lblHp, _lblMp, _lblExp]);
        values.SetFlowBreak(_lblMp, true); // EXP는 자릿수가 길어 다음 줄
        info.Controls.Add(values, 0, 0);
        info.SetColumnSpan(values, 2);
        info.Controls.Add(_lblHunt, 0, 1);
        var rates = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _lblPerMin.Margin = new Padding(0, 0, 10, 0); _lblPerHour.Margin = Padding.Empty;
        rates.Controls.AddRange([_lblPerMin, _lblPerHour]);
        info.Controls.Add(rates, 1, 1);

        // ---- 4줄: MP 자동 입력
        var mpRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(6, 2, 6, 0), WrapContents = false };
        _mpAuto.Text = "MP"; _mpAuto.AutoSize = true; _mpAuto.Margin = new Padding(3, 5, 0, 0);
        _mpAuto.Checked = _cfg.MpAutoEnabled;
        _mpAuto.CheckedChanged += (_, _) => { _cfg.MpAutoEnabled = _mpAuto.Checked; SaveConfig(); UpdateAutoInfo(); };
        _mpThreshold.Minimum = 0; _mpThreshold.Maximum = 99_999_999; _mpThreshold.Width = 72;
        _mpThreshold.Value = Math.Clamp(_cfg.MpThreshold, 0, 99_999_999);
        _mpThreshold.ValueChanged += (_, _) => { _cfg.MpThreshold = (int)_mpThreshold.Value; SaveConfig(); };
        _mpKey.Width = 46;
        _mpKey.SetKey(KeyUtil.Parse(_cfg.MpKey));
        _mpKey.KeyAssigned += (_, _) => { _cfg.MpKey = KeyUtil.Format(_mpKey.AssignedKey); SaveConfig(); };
        mpRow.Controls.Add(_mpAuto);
        mpRow.Controls.Add(_mpThreshold);
        mpRow.Controls.Add(new Label { Text = "미만이면", AutoSize = true, Margin = new Padding(2, 6, 2, 0) });
        mpRow.Controls.Add(_mpKey);
        mpRow.Controls.Add(new Label { Text = "키 자동 입력", AutoSize = true, Margin = new Padding(2, 6, 2, 0) });

        // ---- 5줄: 자동 입력 상태 + 그래프/오버레이/중지
        var bottomRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 4, Padding = new Padding(8, 0, 8, 0) };
        bottomRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottomRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottomRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottomRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _autoInfo.AutoSize = true; _autoInfo.ForeColor = Theme.Yellow; _autoInfo.Margin = new Padding(0, 7, 0, 0);
        _btnGraph.Text = "그래프";
        _btnGraph.AutoSize = true;
        _btnGraph.Click += (_, _) => ToggleGraph();
        bottomRow.Controls.Add(_autoInfo, 0, 0);
        bottomRow.Controls.Add(_btnGraph, 1, 0);
        _btnOverlay.AutoSize = true;
        _btnOverlay.Click += (_, _) => ToggleOverlay();
        bottomRow.Controls.Add(_btnOverlay, 2, 0);
        _btnReset.AutoSize = true;
        _btnReset.Click += (_, _) => StopHunt("버튼");
        bottomRow.Controls.Add(_btnReset, 3, 0);

        // ---- 상태줄 (게임 창 맞춤/오류 등 필요한 안내만)
        _status.Dock = DockStyle.Bottom;
        _status.Height = 20;
        _status.ForeColor = Theme.Muted;
        _status.TextAlign = ContentAlignment.MiddleLeft;

        BuildSettings();

        // Dock=Top은 나중에 추가한 것이 위로 감 → 역순으로 추가
        Controls.Add(_settings);
        Controls.Add(bottomRow);
        Controls.Add(mpRow);
        Controls.Add(info);
        Controls.Add(top);
        Controls.Add(_status);
    }

    private void BuildSettings()
    {
        _settings.Dock = DockStyle.Fill;
        _settings.Visible = false;
        _settings.Padding = new Padding(6, 4, 6, 0);

        // ---- 영역 도구
        var regionBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        regionBar.Controls.Add(MakeButton("영역 추가", async (_, _) => await AddRegionAsync()));
        regionBar.Controls.Add(MakeButton("영역 재지정", async (_, _) => await ReselectAsync()));
        regionBar.Controls.Add(MakeButton("삭제", (_, _) => RemoveSelected()));
        regionBar.Controls.Add(MakeButton("1회 읽기", async (_, _) => await ReadAllAsync(force: true)));
        regionBar.Controls.Add(MakeButton("게임 창 기준 저장", (_, _) => SaveGameReference()));
        regionBar.Controls.Add(MakeButton("영역 자동 맞춤", async (_, _) => await FitToGameAsync(quiet: false)));

        // ---- 옵션
        var optBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        Label L(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(8, 7, 2, 0) };

        // 단축키 입력칸: 입력하는 동안만 전역 단축키를 잠시 해제하고(입력칸이 키를 받도록),
        // 키를 지정하면 즉시 다시 등록. 입력칸에서 다른 창(게임)으로 바로 넘어가도 다시 등록됨(Deactivate)
        optBar.Controls.Add(L("시작/일시정지 단축키"));
        _hotkey.Width = 90; _hotkey.AllowModifiers = true;
        _hotkey.SetKey(KeyUtil.Parse(_cfg.StartHotkey));
        _hotkey.Enter += (_, _) => SuspendHotkeys();
        _hotkey.Leave += (_, _) => RegisterStartHotkey();
        _hotkey.KeyAssigned += (_, _) => AssignHotkey(_hotkey, isStart: true);
        optBar.Controls.Add(_hotkey);

        optBar.Controls.Add(L("중지 단축키"));
        _resetHotkey.Width = 90; _resetHotkey.AllowModifiers = true;
        _resetHotkey.SetKey(KeyUtil.Parse(_cfg.ResetHotkey));
        _resetHotkey.Enter += (_, _) => SuspendHotkeys();
        _resetHotkey.Leave += (_, _) => RegisterStartHotkey();
        _resetHotkey.KeyAssigned += (_, _) => AssignHotkey(_resetHotkey, isStart: false);
        optBar.Controls.Add(_resetHotkey);
        Deactivate += (_, _) => { if (ActiveControl is KeyCaptureBox) RegisterStartHotkey(); };

        optBar.Controls.Add(L("기본 주기(ms)"));
        _interval.Minimum = 100; _interval.Maximum = 3_600_000; _interval.Increment = 100; _interval.Width = 70;
        if (_cfg.IntervalMs <= 0) _cfg.IntervalMs = _cfg.IntervalSec * 1000; // 구버전 설정 변환
        _interval.Value = Math.Clamp(_cfg.IntervalMs, 100, 3_600_000);
        _interval.ValueChanged += (_, _) => { _cfg.IntervalMs = (int)_interval.Value; _timer.Interval = _cfg.IntervalMs; SaveConfig(); };
        optBar.Controls.Add(_interval);

        optBar.Controls.Add(L("MP 입력 간격(ms)"));
        _mpCooldown.Minimum = 100; _mpCooldown.Maximum = 60_000; _mpCooldown.Increment = 100; _mpCooldown.Width = 65;
        _mpCooldown.Value = Math.Clamp(_cfg.MpCooldownMs, 100, 60_000);
        _mpCooldown.ValueChanged += (_, _) => { _cfg.MpCooldownMs = (int)_mpCooldown.Value; SaveConfig(); };
        optBar.Controls.Add(_mpCooldown);

        _logEnabled.Text = "CSV 기록"; _logEnabled.AutoSize = true; _logEnabled.Margin = new Padding(10, 6, 0, 0);
        _logEnabled.Checked = _cfg.LogEnabled;
        _logEnabled.CheckedChanged += (_, _) => { _cfg.LogEnabled = _logEnabled.Checked; SaveConfig(); };
        _onlyChange.Text = "바뀔 때만"; _onlyChange.AutoSize = true; _onlyChange.Margin = new Padding(4, 6, 0, 0);
        _onlyChange.Checked = _cfg.OnlyOnChange;
        _onlyChange.CheckedChanged += (_, _) => { _cfg.OnlyOnChange = _onlyChange.Checked; SaveConfig(); };
        optBar.Controls.Add(_logEnabled);
        optBar.Controls.Add(_onlyChange);
        _saveUnknown.Text = "인식 실패 이미지 저장"; _saveUnknown.AutoSize = true; _saveUnknown.Margin = new Padding(10, 6, 0, 0);
        _saveUnknown.Checked = _cfg.SaveUnknown;
        _saveUnknown.CheckedChanged += (_, _) => { _cfg.SaveUnknown = _saveUnknown.Checked; SaveConfig(); };
        optBar.Controls.Add(_saveUnknown);
        optBar.Controls.Add(MakeButton("폴더", (_, _) =>
        {
            Directory.CreateDirectory(LogDir);
            System.Diagnostics.Process.Start("explorer.exe", LogDir);
        }));

        optBar.Controls.Add(L("OCR 확대"));
        _scale.Minimum = 1; _scale.Maximum = 6; _scale.DecimalPlaces = 1; _scale.Increment = 0.1m; _scale.Width = 50;
        _scale.Value = Math.Clamp(_cfg.Scale, 1m, 6m);
        _scale.ValueChanged += (_, _) => { _cfg.Scale = _scale.Value; SaveConfig(); };
        optBar.Controls.Add(_scale);

        // ---- 영역 목록
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(TextCol(nameof(RegionItem.Name), "이름", false));
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = nameof(RegionItem.Role), DataPropertyName = nameof(RegionItem.Role), HeaderText = "용도",
            DataSource = Roles, FlatStyle = FlatStyle.Flat
        });
        _grid.Columns.Add(TextCol(nameof(RegionItem.IntervalMs), "주기(ms, 0=기본)", false));
        var valueCol = TextCol(nameof(RegionItem.Value), "값", true);
        valueCol.DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
        _grid.Columns.Add(valueCol);
        _grid.Columns.Add(TextCol(nameof(RegionItem.Mode), "인식 방식", true));
        _grid.DataSource = _regions;
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, _) => SaveConfig();
        _grid.SelectionChanged += (_, _) => ShowPreview();

        // ---- 미리보기 / 글자색 / 학습
        var previewBox = new Panel { Dock = DockStyle.Bottom, Height = 176, Padding = new Padding(0, 4, 0, 0) };
        var previewTitle = new Label { Text = "선택 영역 미리보기  —  왼쪽: 화면 / 오른쪽: 인식 이미지", Dock = DockStyle.Top, Height = 20, ForeColor = Theme.Muted };
        var keyBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        keyBar.Controls.Add(MakeButton("글자색 지정", (_, _) => StartPickColor()));
        keyBar.Controls.Add(MakeButton("글자색 해제", async (_, _) => await ClearKeyColorAsync()));
        keyBar.Controls.Add(MakeButton("정답 입력해 학습", async (_, _) => await LearnAsync()));
        keyBar.Controls.Add(MakeButton("학습 초기화", (_, _) => ResetTemplates()));
        _tplInfo.AutoSize = true; _tplInfo.Margin = new Padding(8, 8, 3, 0); _tplInfo.ForeColor = Theme.Muted;
        keyBar.Controls.Add(_tplInfo);

        var pics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        pics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (var pb in new[] { _shotPreview, _preview })
        {
            pb.Dock = DockStyle.Fill;
            pb.SizeMode = PictureBoxSizeMode.Zoom;
            pb.BackColor = Theme.BgTertiary;
        }
        _shotPreview.MouseClick += async (_, e) => await PickColorAtAsync(e.Location);
        pics.Controls.Add(_shotPreview, 0, 0);
        pics.Controls.Add(_preview, 1, 0);
        previewBox.Controls.Add(pics);
        previewBox.Controls.Add(keyBar);
        previewBox.Controls.Add(previewTitle);

        // ---- 오버레이 표시 항목
        var overlayBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 2, 0, 4) };
        overlayBar.Controls.Add(new Label { Text = "오버레이 표시", AutoSize = true, Margin = new Padding(8, 6, 6, 0), ForeColor = Theme.Muted });
        foreach (var (key, label) in OverlayItems)
        {
            var cb = new CheckBox { Text = label, AutoSize = true, Checked = ShowItem(key), Margin = new Padding(4, 4, 4, 0) };
            cb.CheckedChanged += (_, _) =>
            {
                _cfg.OverlayHidden.Remove(key);
                if (!cb.Checked) _cfg.OverlayHidden.Add(key);
                SaveConfig();
                UpdateDisplay();
            };
            overlayBar.Controls.Add(cb);
        }

        var sep = new Label { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line, Tag = "keep" };
        _settings.Controls.Add(_grid);
        _settings.Controls.Add(previewBox);
        _settings.Controls.Add(overlayBar);
        _settings.Controls.Add(optBar);
        _settings.Controls.Add(regionBar);
        _settings.Controls.Add(sep);
    }

    private void ToggleSettings()
    {
        bool show = !_settings.Visible;
        _settings.Visible = show;
        _btnSettings.Text = show ? "⚙ 닫기" : "⚙ 설정";
        FormBorderStyle = show ? FormBorderStyle.Sizable : FormBorderStyle.FixedSingle;
        MaximizeBox = show;
        ClientSize = show ? new Size(SettingsWidth, SettingsHeight) : new Size(CompactWidth, CompactHeight);
        if (show) ShowPreview();
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += onClick;
        return b;
    }

    private static DataGridViewTextBoxColumn TextCol(string prop, string header, bool readOnly) =>
        new() { Name = prop, DataPropertyName = prop, HeaderText = header, ReadOnly = readOnly };

    // =====================================================================
    // 시작/정지 + 전역 단축키
    // =====================================================================

    /// <summary>시작/일시정지, 중지 전역 단축키 등록. 반환: 모두 성공 여부</summary>
    private bool RegisterStartHotkey()
    {
        var failed = new List<string>();
        var done = new List<string>();
        foreach (var (id, text) in new[] { (HotkeyId, _cfg.StartHotkey), (ResetHotkeyId, _cfg.ResetHotkey) })
        {
            var k = KeyUtil.Parse(text);
            if (!GlobalHotkey.Register(Handle, id, k)) failed.Add(KeyUtil.Format(k));
            else if (k != Keys.None) done.Add(KeyUtil.Format(k));
        }
        AppLog.Write($"단축키 등록: 성공 [{string.Join(", ", done)}]" + (failed.Count > 0 ? $" 실패 [{string.Join(", ", failed)}]" : ""));
        if (failed.Count > 0)
            SetStatus($"단축키 {string.Join(", ", failed)} 등록 실패 (다른 프로그램이 사용 중) → 설정에서 다른 키로 바꾸세요");
        UpdateStartButton();
        return failed.Count == 0;
    }

    /// <summary>단축키 입력칸에 들어가 있는 동안 전역 단축키 잠시 해제 (입력칸이 그 키를 받을 수 있게)</summary>
    private void SuspendHotkeys()
    {
        GlobalHotkey.Unregister(Handle, HotkeyId);
        GlobalHotkey.Unregister(Handle, ResetHotkeyId);
    }

    /// <summary>입력칸에서 키를 지정했을 때: 중복 확인 → 저장 → 즉시 다시 등록 (입력칸에서 빠져나옴)</summary>
    private void AssignHotkey(KeyCaptureBox box, bool isStart)
    {
        var k = box.AssignedKey;
        string name = isStart ? "시작/일시정지" : "중지";
        var other = KeyUtil.Parse(isStart ? _cfg.ResetHotkey : _cfg.StartHotkey);
        if (k != Keys.None && k == other)
        {
            box.SetKey(KeyUtil.Parse(isStart ? _cfg.StartHotkey : _cfg.ResetHotkey)); // 원래 키로 되돌림
            SetStatus($"{KeyUtil.Format(k)}는 이미 {(isStart ? "중지" : "시작/일시정지")} 단축키입니다 → 다른 키를 누르세요");
            return;
        }
        if (isStart) _cfg.StartHotkey = KeyUtil.Format(k); else _cfg.ResetHotkey = KeyUtil.Format(k);
        SaveConfig();
        AppLog.Write($"단축키 변경: {name} = {(k == Keys.None ? "(없음)" : KeyUtil.Format(k))}");
        _btnSettings.Focus(); // 입력칸에서 빠져나옴 (다시 누른 키가 입력칸으로 가지 않도록)
        if (RegisterStartHotkey())
            SetStatus(k == Keys.None ? $"{name} 단축키 해제됨" : $"{name} 단축키 {KeyUtil.Format(k)} 적용됨");
    }

    /// <summary>중지: 읽기를 멈추고 사냥 기록(시간·누적·그래프)을 모두 초기화</summary>
    private void StopHunt(string source)
    {
        bool wasRunning = _timer.Enabled;
        string summary = $"경과 {_hunt.Elapsed:hh\\:mm\\:ss}, 누적 {_hunt.Gained:+#,0;-#,0;0}";
        _timer.Stop();
        _hunt.Reset();
        AppLog.Write($"중지({source}): 사냥 기록 초기화 ({summary})" + (wasRunning ? ", 읽기 멈춤" : ""));
        UpdateStartButton();
        UpdateDisplay();
        _graph?.Tick();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == GlobalHotkey.WM_HOTKEY && m.WParam == HotkeyId) ToggleRunning("단축키");
        if (m.Msg == GlobalHotkey.WM_HOTKEY && m.WParam == ResetHotkeyId) StopHunt("단축키");
        base.WndProc(ref m);
    }

    private void UpdateStartButton()
    {
        string hk = KeyUtil.Format(KeyUtil.Parse(_cfg.StartHotkey));
        string label = _timer.Enabled ? "❚❚ 일시정지" : _hunt.Started ? "▶ 재개" : "▶ 시작";
        _btnStart.Text = hk.Length > 0 ? $"{label}  ({hk})" : label;
        if (_timer.Enabled) Theme.StyleButton(_btnStart, Theme.Accent, Theme.AccentHover); else Theme.StyleButton(_btnStart, Theme.Green, Theme.GreenHover);
        string rk = KeyUtil.Format(KeyUtil.Parse(_cfg.ResetHotkey));
        _btnReset.Text = rk.Length > 0 ? $"■ 중지 ({rk})" : "■ 중지";
        if (_hunt.Started || _timer.Enabled) Theme.StyleButton(_btnReset, Theme.Red, Theme.RedHover); else Theme.StyleButton(_btnReset, Theme.ButtonBg, Theme.ButtonHover);
    }

    /// <summary>
    /// 시작/일시정지: 캡처와 사냥 집계를 함께 켜고 끔. 일시정지는 시간·누적 유지,
    /// 시작/재개 시에는 현재 경험치를 다시 읽어 기준으로 삼음 (일시정지 동안 오른 경험치는 누적에 넣지 않음)
    /// </summary>
    private void ToggleRunning(string source)
    {
        if (_timer.Enabled)
        {
            _timer.Stop();
            _hunt.Pause();
            AppLog.Write($"일시정지({source}): 경과 {_hunt.Elapsed:hh\\:mm\\:ss}, 누적 {_hunt.Gained:+#,0;-#,0;0}");
        }
        else
        {
            bool resume = _hunt.Started;
            _timer.Interval = (int)_interval.Value;
            _timer.Start();
            if (ByRole("EXP") is not null) _hunt.Start();
            AppLog.Write($"{(resume ? "재개" : "시작")}({source}): 현재 EXP 다시 읽는 중" + (ByRole("EXP") is null ? " (EXP 영역 없음)" : ""));
            _ = ReadAllAsync(force: true);
        }
        UpdateStartButton();
        UpdateDisplay();
    }

    // =====================================================================
    // 영역 관리
    // =====================================================================

    private async Task<Rectangle?> PickRegionAsync()
    {
        Hide();
        _overlay.Hide();
        await Task.Delay(250); // 창이 사라진 뒤 캡처
        try
        {
            using var f = new SelectForm();
            return f.ShowDialog() == DialogResult.OK ? f.Selected : null;
        }
        finally
        {
            Show();
            if (_cfg.OverlayVisible) _overlay.Show();
            Activate();
        }
    }

    private async Task AddRegionAsync()
    {
        if (await PickRegionAsync() is not { } r) return;
        var item = new RegionItem { Name = $"영역{_regions.Count + 1}", Rect = r };
        _regions.Add(item);
        SaveConfig();
        _grid.ClearSelection();
        _grid.Rows[^1].Selected = true;
        await ReadOneAsync(item, log: false);
    }

    private async Task ReselectAsync()
    {
        if (CurrentItem() is not { } item) return;
        if (await PickRegionAsync() is not { } r) return;
        item.Rect = r;
        SaveConfig();
        await ReadOneAsync(item, log: false);
    }

    private void RemoveSelected()
    {
        if (CurrentItem() is not { } item) return;
        item.Preview?.Dispose();
        item.Shot?.Dispose();
        _regions.Remove(item);
        SaveConfig();
        ShowPreview();
    }

    private RegionItem? CurrentItem() =>
        _grid.CurrentRow?.Index is int i && i >= 0 && i < _regions.Count ? _regions[i] : null;

    // =====================================================================
    // 읽기
    // =====================================================================

    /// <param name="force">true면 영역별 주기와 관계없이 모두 읽음 (1회 읽기, 시작 직후)</param>
    private async Task ReadAllAsync(bool force = false)
    {
        if (_busy || _regions.Count == 0) return;

        // 영역별 주기가 된 것만 읽음 (주기 0 = 기본 주기마다). 타이머 오차를 감안해 기본 주기의 절반 여유
        var now = DateTime.Now;
        double slack = (double)_interval.Value / 2;
        var items = _regions.Where(i => force || i.IntervalMs <= 0 || (now - i.LastRead).TotalMilliseconds >= i.IntervalMs - slack).ToList();
        if (items.Count == 0) return;

        _busy = true;
        try
        {
            // 영역들을 감싸는 사각형이 작으면 한 번만 캡처해서 잘라 씀 (캡처가 가장 느린 구간)
            var union = items.Select(i => i.Rect).Aggregate(Rectangle.Union);
            long sumArea = items.Sum(i => (long)i.W * i.H);
            using var all = (long)union.Width * union.Height <= sumArea * 4 ? OcrService.Capture(union) : null;

            foreach (var item in items)
            {
                var pre = all?.Clone(new Rectangle(item.X - union.X, item.Y - union.Y, item.W, item.H),
                                     System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                await ReadOneAsync(item, log: true, pre);
                item.LastRead = now;
            }
            await ProcessRolesAsync(items.ToHashSet());
            await AutoRefindIfLostAsync();
            UpdateValues();
        }
        finally { _busy = false; }
    }

    private Task<string> RunOcrAsync(Bitmap img) =>
        _ocr?.RecognizeAsync(img) ?? throw new InvalidOperationException("Windows OCR 없음 - 글자색 지정 후 학습 필요");

    /// <param name="preCaptured">미리 캡처한 이미지(소유권 이전). 없으면 직접 캡처</param>
    private async Task ReadOneAsync(RegionItem item, bool log, Bitmap? preCaptured = null)
    {
        string prev = item.Value;
        try
        {
            var shot = preCaptured ?? OcrService.Capture(item.Rect);
            item.Shot?.Dispose();
            item.Shot = shot;

            if (item.KeyColor is int argb)
            {
                // 글자색 키잉 → 템플릿 매칭 (학습 전이면 키잉 이미지로 OCR)
                var mask = GlyphMatcher.Key(shot, Color.FromArgb(argb), HueTolerance);
                item.Mask = mask;
                // 미리보기는 다크 테마용(어두운 배경 + 밝은 글자), 설정 화면이 열려 있을 때만 생성
                if (_settings.Visible)
                {
                    item.Preview?.Dispose();
                    item.Preview = GlyphMatcher.ToBitmap(mask, fg: Theme.Header, bg: Theme.BgTertiary);
                }

                if (_matcher.Templates.Count > 0)
                {
                    item.Raw = _matcher.Recognize(mask);
                    item.Value = item.Raw;
                    if (item.Raw.Contains('?')) SaveUnknownShot(item, shot);
                }
                else
                {
                    using var img = GlyphMatcher.ToBitmap(mask); // OCR 입력은 흰 배경 + 검은 글자
                    string raw = await RunOcrAsync(img);
                    item.Raw = raw.Replace("\n", " / ");
                    item.Value = item.NumericOnly ? OcrService.ExtractNumbers(raw) : raw.Replace("\n", " ").Trim();
                }
            }
            else
            {
                item.Mask = null;
                var img = OcrService.Preprocess(shot, (float)_scale.Value);
                item.Preview?.Dispose();
                item.Preview = img;

                string raw = await RunOcrAsync(img);
                item.Raw = raw.Replace("\n", " / ");
                item.Value = item.NumericOnly ? OcrService.ExtractNumbers(raw) : raw.Replace("\n", " ").Trim();
            }
        }
        catch (Exception ex)
        {
            item.Raw = "";
            item.Value = "오류: " + ex.Message;
        }
        item.Time = DateTime.Now.ToString("HH:mm:ss");

        // 설정 화면이 열려 있을 때만 목록/미리보기 갱신 (불필요한 화면 갱신 줄임)
        if (_settings.Visible)
        {
            int idx = _regions.IndexOf(item);
            if (idx >= 0) _regions.ResetItem(idx);
            if (CurrentItem() == item) ShowPreview();
        }

        if (log && _logEnabled.Checked && (!_onlyChange.Checked || item.Value != prev))
            AppendCsv(item);
    }

    // =====================================================================
    // 게임 창 기준 (영역 자동 맞춤)
    // =====================================================================

    /// <summary>현재 영역들이 올라가 있는 게임 창의 제목과 게임 화면 위치·크기를 기준으로 저장</summary>
    private void SaveGameReference()
    {
        var item = _regions.FirstOrDefault(r => r.Role != "") ?? _regions.FirstOrDefault();
        if (item is null) { SetStatus("먼저 영역을 지정하세요."); return; }
        var h = GameLocator.RootWindowAt(new Point(item.X + item.W / 2, item.Y + item.H / 2));
        string title = GameLocator.TitleOf(h);
        if (h == IntPtr.Zero || title.Length == 0 || h == Handle || h == _overlay.Handle)
        {
            SetStatus("영역 위치에서 게임 창을 찾지 못했습니다. 게임 창이 영역 위에 보이는지 확인하세요.");
            return;
        }
        // 픽셀 판정은 비율(가로/세로)을 재는 데만 쓰고, 실제 기준은 비율 계산 결과로 저장 (이후 자동 맞춤과 동일한 방식)
        if (GameLocator.DetectCanvas(h) is not { } detected) { SetStatus("게임 화면 영역을 계산하지 못했습니다."); return; }
        var canvas = GameLocator.FitCanvas(GameLocator.ClientRectOnScreen(h), (double)detected.Width / detected.Height);
        _cfg.GameTitle = title;
        _cfg.RefCanvas = canvas;
        _lastClient = GameLocator.ClientRectOnScreen(h);
        SaveConfig();
        SetStatus($"게임 창 기준 저장: {title}");
    }

    /// <summary>게임 창을 찾아 게임 화면의 위치·크기 변화만큼 영역과 오버레이를 변환</summary>
    private async Task<bool> FitToGameAsync(bool quiet)
    {
        if (string.IsNullOrEmpty(_cfg.GameTitle) || _cfg.RefCanvasW <= 0 || _finding) return false;
        _finding = true;
        _lastFindAt = DateTime.Now;
        try
        {
            string title = _cfg.GameTitle;
            var res = await Task.Run(() =>
            {
                var h = GameLocator.FindWindow(title);
                if (h == IntPtr.Zero) return ((Rectangle client, Rectangle? canvas)?)null;
                // 게임 화면 = 클라이언트 영역에 기준 비율로 가운데 맞춤 (픽셀 분석 안 함 → 겹친 창에 영향 없음)
                var client = GameLocator.ClientRectOnScreen(h);
                double aspect = (double)_cfg.RefCanvasW / _cfg.RefCanvasH;
                return (client, (Rectangle?)GameLocator.FitCanvas(client, aspect));
            });
            if (res is not { } r)
            {
                if (!quiet) SetStatus("게임 창을 찾지 못했습니다. 게임을 켠 뒤 [⚙ 설정 → 영역 자동 맞춤]");
                return false;
            }
            _lastClient = r.client;
            if (r.canvas is not { } canvas)
            {
                if (!quiet) SetStatus("게임 화면을 계산하지 못했습니다.");
                return false;
            }

            var from = _cfg.RefCanvas;
            if (canvas != from)
            {
                foreach (var item in _regions) item.Rect = GameLocator.Map(item.Rect, from, canvas);
                var ov = GameLocator.Map(new Rectangle(_overlay.Location, new Size(1, 1)), from, canvas);
                _overlay.Location = ov.Location;
                _cfg.RefCanvas = canvas;
                _regions.ResetBindings();
                SaveConfig();
                EnsureOverlayOnScreen();
                SetStatus($"게임 창에 맞춰 영역 조정됨 (x{(double)canvas.Height / from.Height:F2})");
            }
            else SetStatus("게임 창 연결됨");
            return true;
        }
        finally { _finding = false; }
    }

    /// <summary>1초마다 게임 창 클라이언트 영역을 확인해, 이동/크기 변경 시 영역 자동 맞춤</summary>
    private async Task WatchGameWindowAsync()
    {
        if (string.IsNullOrEmpty(_cfg.GameTitle) || _cfg.RefCanvasW <= 0 || _finding) return;
        var h = GameLocator.FindWindow(_cfg.GameTitle);
        if (h == IntPtr.Zero) return;
        var client = GameLocator.ClientRectOnScreen(h);
        if (client == _lastClient) return;
        await Task.Delay(300); // 이동/크기 조절이 끝날 때까지 잠깐 대기
        await FitToGameAsync(quiet: true);
    }

    /// <summary>용도 영역이 3초 이상 전부 인식 불가면 게임 화면을 다시 계산 (5초 간격)</summary>
    private async Task AutoRefindIfLostAsync()
    {
        int roleCount = _regions.Count(r => r.Role != "");
        if (string.IsNullOrEmpty(_cfg.GameTitle) || roleCount == 0 || UnreadableRoles().Count < roleCount)
        {
            _allUnreadableSince = null;
            return;
        }
        _allUnreadableSince ??= DateTime.Now;
        if ((DateTime.Now - _allUnreadableSince.Value).TotalSeconds >= 3 && (DateTime.Now - _lastFindAt).TotalSeconds >= 5)
            await FitToGameAsync(quiet: true);
    }

    /// <summary>오버레이가 어느 모니터에도 걸치지 않으면 주 모니터로 이동</summary>
    private void EnsureOverlayOnScreen()
    {
        var b = new Rectangle(_overlay.Location, _overlay.Size.IsEmpty ? new Size(200, 120) : _overlay.Size);
        if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(b))) return;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        _overlay.Location = new Point(wa.X + 100, wa.Y + 100);
    }

    // =====================================================================
    // 용도별 처리 (EXP 집계, MP 자동 입력)
    // =====================================================================

    private RegionItem? ByRole(string role) => _regions.FirstOrDefault(r => r.Role == role);

    /// <summary>값이 순수 숫자일 때만 파싱 ('?' 등 인식 실패 포함 시 무시)</summary>
    private static bool TryNumber(RegionItem? item, out long value)
    {
        value = 0;
        return item is not null && item.Value.Length > 0 && item.Value.All(char.IsDigit) && long.TryParse(item.Value, out value);
    }

    /// <param name="justRead">이번에 실제로 새로 읽은 영역 (안 읽은 영역의 이전 값으로 판정하지 않도록)</param>
    private async Task ProcessRolesAsync(HashSet<RegionItem> justRead)
    {
        var expItem = ByRole("EXP");
        if (expItem is not null && justRead.Contains(expItem) && TryNumber(expItem, out long exp)) _hunt.Feed(exp);

        if (!_mpAuto.Checked) { _mpLowStreak = 0; SetAutoMsg(""); return; }
        var mpItem = ByRole("MP");
        if (mpItem is null) { _mpLowStreak = 0; SetAutoMsg("MP 영역 없음"); return; }
        if (!justRead.Contains(mpItem)) return; // MP를 새로 읽은 때에만 판정

        // 화면 인식 확인: HP/MP가 정상 숫자로 읽혀야 자동 입력 (EXP 인식 불가는 자동 입력에 영향 없음)
        var unreadable = UnreadableRoles().Where(r => r != "EXP").ToList();
        if (unreadable.Count > 0)
        {
            _mpLowStreak = 0;
            SetAutoMsg($"⚠ {string.Join(",", unreadable)} 인식 불가 → 자동 입력 중지");
            return;
        }
        TryNumber(mpItem, out long mp);
        if (mp >= (long)_mpThreshold.Value) { _mpLowStreak = 0; SetAutoMsg(_keyCount > 0 ? $"자동 입력 {_keyCount}회" : ""); return; }

        // 일시적 오인식 방지: 2회 연속 기준 미만일 때만 입력
        if (++_mpLowStreak < 2) return;
        if ((DateTime.Now - _lastKeyAt).TotalMilliseconds < (double)_mpCooldown.Value) return;

        var key = _mpKey.AssignedKey & Keys.KeyCode;
        if (key == Keys.None) { SetAutoMsg("⚠ 입력할 키를 지정하세요"); return; }
        var center = new Point(mpItem.X + mpItem.W / 2, mpItem.Y + mpItem.H / 2);
        if (!KeySender.IsGameForeground(center))
        {
            SetAutoMsg("게임 창이 활성 상태가 아니라 입력 안 함");
            return;
        }
        _lastKeyAt = DateTime.Now;
        await KeySender.PressAsync(key);
        _keyCount++;
        SetAutoMsg($"자동 입력 {_keyCount}회 ({_lastKeyAt:HH:mm:ss})");
    }

    /// <summary>용도가 지정됐는데 정상 숫자로 읽히지 않은 영역 이름 목록</summary>
    private List<string> UnreadableRoles() =>
        _regions.Where(r => r.Role != "" && !TryNumber(r, out _)).Select(r => r.Role).ToList();

    private void SetAutoMsg(string msg)
    {
        _lastAutoMsg = msg;
        UpdateAutoInfo();
    }

    private void UpdateAutoInfo()
    {
        string text = _mpAuto.Checked ? _lastAutoMsg : "";
        if (_autoInfo.Text != text) _autoInfo.Text = text;
    }

    // =====================================================================
    // 표시 (기본 화면 + 오버레이)
    // =====================================================================

    private void ToggleOverlay()
    {
        if (_overlay.Visible) _overlay.Hide(); else _overlay.Show();
        _cfg.OverlayVisible = _overlay.Visible;
        SaveConfig();
        UpdateOverlayButton();
        UpdateDisplay();
    }

    /// <summary>경험치 그래프 창 열기/닫기 (처음 열 때 메인 창 오른쪽에 배치)</summary>
    private void ToggleGraph()
    {
        if (_graph is { Visible: true }) { _graph.Hide(); return; }
        if (_graph is null)
        {
            _graph = new GraphForm(_hunt);
            var wa = Screen.FromControl(this).WorkingArea;
            int x = Right + 8 + _graph.Width <= wa.Right ? Right + 8 : Math.Max(wa.Left, Left - 8 - _graph.Width);
            _graph.Location = new Point(x, Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - _graph.Height)));
        }
        _graph.Show(this);
        _graph.Tick();
    }

    private void UpdateOverlayButton() => _btnOverlay.Text = _overlay.Visible ? "오버레이 끄기" : "오버레이 켜기";

    private static string Fmt(string? v) =>
        string.IsNullOrEmpty(v) ? "-" : long.TryParse(v, out long n) ? n.ToString("#,0") : v;

    private static void SetText(Label l, string t) { if (l.Text != t) l.Text = t; }

    /// <summary>현재값 표시 (읽을 때마다)</summary>
    private void UpdateValues()
    {
        // 등록된 용도만 표시
        foreach (var (lbl, role) in new[] { (_lblHp, "HP"), (_lblMp, "MP"), (_lblExp, "EXP") })
        {
            var item = ByRole(role);
            if (lbl.Visible != (item is not null)) lbl.Visible = item is not null;
            if (item is not null) SetText(lbl, $"{role}  {Fmt(item.Value)}");
        }
        UpdateAutoInfo();
    }

    /// <summary>사냥 통계 + 오버레이 (1초마다, 시작/정지 시)</summary>
    private void UpdateDisplay()
    {
        UpdateValues();
        var el = _hunt.Elapsed;
        string time = $"{(int)el.TotalHours:00}:{el.Minutes:00}:{el.Seconds:00}";
        string state = _timer.Enabled ? "사냥 중" : _hunt.Started ? "일시정지" : "대기";
        SetText(_lblHunt, $"{state} {time}   누적 {_hunt.Gained:+#,0;-#,0;0}");
        SetText(_lblPerMin, $"분당 {_hunt.PerMinute:#,0}");
        SetText(_lblPerHour, $"시간당 {_hunt.PerHour:#,0}");
        _lblPerMin.ForeColor = Theme.PerMinuteColor(_hunt.PerMinute);
        _lblPerHour.ForeColor = Theme.PerHourColor(_hunt.PerHour);
        UpdateOverlay(state, time);
    }

    private void UpdateOverlay(string state, string time)
    {
        if (!_overlay.Visible) return;
        if (!_overlay.GraphMode) { _overlay.SetLines(BuildOverlayLines(state, time, _hunt.PerMinute, _hunt.PerHour)); return; }

        // 그래프 모드: 분당·시간당 + 최근 1분 동안의 분당 경험치(직전 60초 기준) 추이
        double perMin = _hunt.PerMinute, perHour = _hunt.PerHour;
        var lines = new List<(string, Color)>
        {
            ($"분당 EXP   {perMin:#,0}", Theme.PerMinuteColor(perMin)),
            ($"시간당 EXP {perHour:#,0}", Theme.PerHourColor(perHour)),
        };
        double tEnd = _hunt.Elapsed.TotalSeconds, t0 = Math.Max(0, tEnd - 60);
        var pts = new List<(double t, double v)>();
        if (_hunt.Started)
        {
            for (double t = t0; t < tEnd; t += 1)
                if (_hunt.RateAt(t) is double r) pts.Add((t, r));
            if (_hunt.RateAt(tEnd) is double now) pts.Add((tEnd, now));
        }
        _overlay.SetGraph(lines, "최근 1분 분당 EXP", pts, t0, t0 + 60);
    }

    /// <summary>오버레이 줄 구성 (항목 선택 반영, 분당/시간당은 구간별 색)</summary>
    private List<(string text, Color color)> BuildOverlayLines(string state, string time, double perMin, double perHour)
    {
        var lines = new List<(string, Color)>();
        void Add(string t, Color? c = null) => lines.Add((t, c ?? Theme.Text));
        if (ShowItem("state"))
        {
            string hk = KeyUtil.Format(KeyUtil.Parse(_cfg.StartHotkey));
            Add(_timer.Enabled || hk.Length == 0 ? $"[{state}]  {time}" : $"[{state}]  {time}   ({hk}: 시작)", Theme.Header);
        }
        if (ShowItem("hpmp"))
        {
            var hpmp = new[] { "HP", "MP" }.Where(r => ByRole(r) is not null).Select(r => $"{r} {Fmt(ByRole(r)!.Value)}");
            if (hpmp.Any()) Add(string.Join("   ", hpmp));
        }
        if (ShowItem("startExp")) Add($"시작 EXP   {(_hunt.StartValue is long sv ? sv.ToString("#,0") : "-")}", Theme.Muted);
        if (ShowItem("curExp")) Add($"현재 EXP   {Fmt(ByRole("EXP")?.Value)}");
        if (ShowItem("gained")) Add($"누적 EXP   {_hunt.Gained:+#,0;-#,0;0}");
        if (ShowItem("perMin")) Add($"분당 EXP   {perMin:#,0}", Theme.PerMinuteColor(perMin));
        if (ShowItem("perHour")) Add($"시간당 EXP {perHour:#,0}", Theme.PerHourColor(perHour));
        if (ShowItem("hourly") && _hunt.Hourly.Count > 0)
        {
            Add("─ 시간별 누적 ─", Theme.Muted);
            foreach (var (hour, gained) in _hunt.Hourly.TakeLast(6))
                Add($"{hour:HH}시  {gained:+#,0;-#,0;0}");
        }
        if (ShowItem("mpAuto") && _mpAuto.Checked) Add($"MP 자동: {_mpKey.Text} {_keyCount}회", Theme.Muted);
        if (ShowItem("warn") && _timer.Enabled && UnreadableRoles() is { Count: > 0 } bad)
            Add($"⚠ 인식 불가: {string.Join(", ", bad)}", Theme.Yellow);
        if (lines.Count == 0) Add("(표시 항목 없음)", Theme.Muted);
        return lines;
    }

    private void ShowPreview()
    {
        if (!_settings.Visible) return;
        var item = CurrentItem();
        _preview.Image = item?.Preview;
        _shotPreview.Image = item?.Shot;
    }

    // =====================================================================
    // 글자색 / 학습
    // =====================================================================

    private void StartPickColor()
    {
        if (CurrentItem()?.Shot is null) { SetStatus("영역을 선택하고 [1회 읽기]를 먼저 하세요."); return; }
        _pickingColor = true;
        _shotPreview.Cursor = Cursors.Cross;
        SetStatus("왼쪽 화면 이미지에서 숫자 안쪽 색을 클릭하세요.");
    }

    private async Task PickColorAtAsync(Point p)
    {
        if (!_pickingColor || CurrentItem() is not { Shot: { } shot } item) return;
        if (ImagePoint(_shotPreview, shot, p) is not { } ip) return;

        var c = shot.GetPixel(ip.X, ip.Y);
        if (c.GetSaturation() < 0.2f || c.GetBrightness() < 0.12f)
        {
            SetStatus("테두리/배경 색입니다. 숫자 안쪽(색이 있는 부분)을 클릭하세요.");
            return;
        }
        _pickingColor = false;
        _shotPreview.Cursor = Cursors.Default;
        item.KeyColor = c.ToArgb();
        SaveConfig();
        await ReadOneAsync(item, log: false);
        SetStatus("글자색 지정됨. 오른쪽에 숫자만 검게 보이면 정상입니다.");
    }

    private async Task ClearKeyColorAsync()
    {
        if (CurrentItem() is not { } item) return;
        item.KeyColor = null;
        SaveConfig();
        await ReadOneAsync(item, log: false);
    }

    private async Task LearnAsync()
    {
        if (CurrentItem() is not { } item) return;
        if (item.KeyColor is null || item.Mask is null)
        {
            SetStatus("학습은 글자색을 지정한 영역에서만 가능합니다.");
            return;
        }
        var mask = item.Mask; // 입력하는 동안 화면이 바뀌어도 지금 이미지로 학습
        string? answer = InputBox("정답 입력", "왼쪽 화면 이미지에 보이는 숫자를 그대로 입력하세요:", item.Value.Replace("?", ""));
        if (string.IsNullOrWhiteSpace(answer)) return;

        var (ok, msg) = _matcher.Learn(mask, answer.Trim());
        SetStatus(msg);
        UpdateTemplateInfo();
        if (ok) await ReadOneAsync(item, log: false);
    }

    private void ResetTemplates()
    {
        if (MessageBox.Show(this, "학습한 숫자 모양을 모두 지울까요?", "학습 초기화", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        _matcher.Templates.Clear();
        _matcher.Save();
        UpdateTemplateInfo();
    }

    private void UpdateTemplateInfo()
    {
        var chars = string.Concat(_matcher.Templates.Select(t => t.Ch).Distinct().OrderBy(c => c));
        _tplInfo.Text = _matcher.Templates.Count == 0 ? "학습된 숫자 없음" : $"학습: {chars} ({_matcher.Templates.Count})";
    }

    /// <summary>Zoom 모드 PictureBox의 클릭 위치 → 원본 이미지 픽셀 좌표</summary>
    private static Point? ImagePoint(PictureBox pb, Image img, Point p)
    {
        float s = Math.Min((float)pb.ClientSize.Width / img.Width, (float)pb.ClientSize.Height / img.Height);
        float ox = (pb.ClientSize.Width - img.Width * s) / 2, oy = (pb.ClientSize.Height - img.Height * s) / 2;
        int x = (int)((p.X - ox) / s), y = (int)((p.Y - oy) / s);
        return x >= 0 && y >= 0 && x < img.Width && y < img.Height ? new Point(x, y) : null;
    }

    private static string? InputBox(string title, string prompt, string initial)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 110), MinimizeBox = false, MaximizeBox = false, Font = new Font("맑은 고딕", 9f)
        };
        var lbl = new Label { Text = prompt, Left = 12, Top = 12, AutoSize = true };
        var tb = new TextBox { Text = initial, Left = 12, Top = 36, Width = 336 };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Left = 192, Top = 72, Width = 75 };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Left = 273, Top = 72, Width = 75 };
        f.Controls.AddRange([lbl, tb, ok, cancel]);
        f.AcceptButton = ok; f.CancelButton = cancel;
        Theme.Apply(f);
        Theme.StyleButton(ok, Theme.Accent, Theme.AccentHover);
        f.HandleCreated += (_, _) => Theme.DarkTitleBar(f);
        return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
    }

    // =====================================================================
    // 기록 / 설정 저장
    // =====================================================================

    /// <summary>
    /// 인식 실패('?') 화면 이미지를 unknown 폴더에 저장 (학습용).
    /// 같은 결과가 반복되면 저장하지 않고, 영역당 1초에 1장, 폴더당 최대 300장.
    /// </summary>
    private void SaveUnknownShot(RegionItem item, Bitmap shot)
    {
        if (!_saveUnknown.Checked) return;
        try
        {
            var now = DateTime.Now;
            if (_lastUnknown.TryGetValue(item, out var last) && (last.raw == item.Raw || (now - last.at).TotalSeconds < 1)) return;
            _lastUnknown[item] = (item.Raw, now);
            Directory.CreateDirectory(UnknownDir);
            if (Directory.GetFiles(UnknownDir, "*.png").Length >= 300) return;
            string name = $"{(item.Role != "" ? item.Role : item.Name)}_{now:yyyyMMdd_HHmmss_fff}_{item.Raw.Replace('?', 'x')}.png";
            shot.Save(Path.Combine(UnknownDir, name), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch { /* 저장 실패는 무시 */ }
    }

    /// <summary>templates.json이 외부에서 바뀌었으면 다시 읽음 (재시작 없이 학습 반영)</summary>
    private void ReloadTemplatesIfChanged()
    {
        var stamp = GlyphMatcher.FileStamp;
        if (stamp == _tplStamp) return;
        _tplStamp = stamp;
        _matcher.Load();
        UpdateTemplateInfo();
    }

    private static void AppendCsv(RegionItem item)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            string path = Path.Combine(LogDir, $"ocr_{DateTime.Now:yyyyMMdd}.csv");
            var sb = new StringBuilder();
            if (!File.Exists(path)) sb.AppendLine("시각,이름,값,인식원문");
            sb.AppendLine(string.Join(",",
                Csv(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.f")), Csv(item.Name), Csv(item.Value), Csv(item.Raw)));
            // 새 파일이면 BOM 포함(엑셀에서 한글 깨짐 방지)
            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch
        {
            // 파일이 엑셀에서 열려 잠긴 경우 등은 이번 기록만 건너뜀
        }
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    private void SetStatus(string text) => _status.Text = "  " + text;

    private void SaveConfig()
    {
        try { _cfg.Save(); } catch { /* 저장 실패는 무시 */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        GlobalHotkey.Unregister(Handle, HotkeyId);
        GlobalHotkey.Unregister(Handle, ResetHotkeyId);
        _timer.Stop();
        _uiTimer.Stop();
        _cfg.OverlayVisible = _overlay.Visible;
        SaveConfig();
        _overlay.Close();
        base.OnFormClosing(e);
    }
}
