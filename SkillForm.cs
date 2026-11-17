using System.ComponentModel;

namespace RegionOcr;

/// <summary>스킬 쿨타임(애드온 창) 자동 입력 규칙 설정 + 실시간 인식 확인</summary>
public sealed class SkillForm : Form
{
    private readonly BindingList<SkillRule> _rules;
    private readonly Action _save;
    private readonly DataGridView _grid = new();
    private readonly ListBox _live = new();
    private readonly Label _liveStatus = new();
    private readonly PictureBox _preview = new();
    private readonly KeyCaptureBox _key = new();
    private Bitmap? _lastPreviewSrc;

    private readonly CooldownReader _reader;

    public SkillForm(BindingList<SkillRule> rules, Action save, CooldownReader reader)
    {
        CaptureGuard.Attach(this); // 화면 캡처에서 제외 (게임 위에 겹쳐도 인식에 섞이지 않게)
        _rules = rules;
        _save = save;
        _reader = reader;
        Text = "스킬 쿨타임 자동 입력";
        Font = Theme.Font;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 520);
        MinimumSize = new Size(520, 420);
        ShowInTaskbar = false;
        BuildUi();
        Theme.Apply(this);
        _live.BackColor = Theme.BgTertiary;
        _live.ForeColor = Theme.Header;
        _live.BorderStyle = BorderStyle.None;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    // 닫기 = 숨김 (실시간 인식 상태 유지)
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    private void BuildUi()
    {
        // ---- 위: 실시간 인식 (왼쪽 목록 / 오른쪽 인식 이미지)
        var liveBox = new Panel { Dock = DockStyle.Top, Height = 170, Padding = new Padding(8, 6, 8, 0) };
        _liveStatus.Dock = DockStyle.Top;
        _liveStatus.Height = 20;
        _liveStatus.ForeColor = Theme.Muted;
        _liveStatus.Text = "애드온 창 찾는 중... (게임에서 스킬을 써서 쿨타임이 보이게 하세요)";
        var liveRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        liveRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        liveRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        _live.Dock = DockStyle.Fill;
        _live.Font = new Font("맑은 고딕", 10f);
        _live.DoubleClick += (_, _) => AddFromLive();
        _preview.Dock = DockStyle.Fill;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        liveRow.Controls.Add(_live, 0, 0);
        liveRow.Controls.Add(_preview, 1, 0);
        liveBox.Controls.Add(liveRow);
        liveBox.Controls.Add(_liveStatus);

        // ---- 버튼 줄
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 4, 4, 0) };
        bar.Controls.Add(Btn("선택 줄로 규칙 추가", (_, _) => AddFromLive()));
        bar.Controls.Add(Btn("직접 추가", (_, _) => AddRule(new SkillRule { Name = "스킬명", Seconds = 1 })));
        bar.Controls.Add(Btn("삭제", (_, _) => RemoveRule()));
        bar.Controls.Add(Btn("숫자 학습", (_, _) => LearnDigits()));
        bar.Controls.Add(new Label { Text = "선택 규칙 키", AutoSize = true, Margin = new Padding(12, 7, 2, 0) });
        _key.Width = 70;
        _key.KeyAssigned += (_, _) =>
        {
            if (Current() is not { } r) return;
            r.Key = KeyUtil.Format(_key.AssignedKey);
            _rules.ResetItem(_rules.IndexOf(r));
            _save();
        };
        bar.Controls.Add(_key);

        // ---- 규칙 목록
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(SkillRule.Enabled), HeaderText = "사용", FillWeight = 40 });
        _grid.Columns.Add(Col(nameof(SkillRule.Name), "스킬명", false, 100));
        _grid.Columns.Add(Col(nameof(SkillRule.Shape), "이름 모양", true, 60));
        _grid.Columns.Add(Col(nameof(SkillRule.Seconds), "N초 이하", false, 60));
        _grid.Columns.Add(Col(nameof(SkillRule.Key), "키", true, 50));
        _grid.Columns.Add(Col(nameof(SkillRule.Status), "상태", true, 90));
        _grid.DataSource = _rules;
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && _rules[e.RowIndex] is { } r) r.Seconds = Math.Clamp(r.Seconds, 0, 9999);
            _save();
        };
        _grid.SelectionChanged += (_, _) => _key.SetKey(KeyUtil.Parse(Current()?.Key));

        var help = new Label
        {
            Dock = DockStyle.Bottom, Height = 54, ForeColor = Theme.Muted, Padding = new Padding(8, 4, 8, 0),
            Text = "· 남은 시간이 N초 이하가 되면 키를 1번 입력 (쿨타임 1회당 1번). N=0이면 쿨이 끝나 창에서 사라질 때 입력\n" +
                   "· 위 목록에서 줄을 골라 추가해야 '이름 모양'이 등록됨 (목록 이름은 OCR 참고값이라 틀려도 됨 → 스킬명만 고치세요)\n" +
                   "· [▶ 시작] 상태이고 기본 화면의 '스킬 자동' 체크가 켜져 있으며 게임 창이 활성일 때만 입력",
        };

        Controls.Add(_grid);
        Controls.Add(bar);
        Controls.Add(liveBox);
        Controls.Add(help);
    }

    private static Button Btn(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += onClick;
        return b;
    }

    private static DataGridViewTextBoxColumn Col(string prop, string header, bool readOnly, float weight) =>
        new() { DataPropertyName = prop, HeaderText = header, ReadOnly = readOnly, FillWeight = weight };

    private SkillRule? Current() =>
        _grid.CurrentRow?.Index is int i && i >= 0 && i < _rules.Count ? _rules[i] : null;

    private void AddFromLive()
    {
        if (_live.SelectedItem is not CooldownLine line)
        {
            MessageBox.Show(this, "위 인식 목록에서 스킬 줄을 먼저 선택하세요.", Text);
            return;
        }
        if (_rules.FirstOrDefault(r => CooldownReader.NameSimilarity(r.NameKey, line.NameKey) >= 0.8) is { } dup)
        {
            MessageBox.Show(this, $"이 스킬은 이미 '{dup.Name}' 규칙으로 등록돼 있습니다.", Text);
            return;
        }
        // 이름은 OCR 참고값 → 실제 스킬명으로 고쳐 쓰도록 안내. 비교는 이름 모양으로 함
        AddRule(new SkillRule { Name = line.Name, OcrName = line.Name, NameKey = line.NameKey, Seconds = 1 });
    }

    /// <summary>
    /// 선택한 줄의 숫자 모양을 정답으로 학습. 숫자는 1초마다 바뀌므로 누른 순간의 모양을 보관해 보여 주고 입력받음
    /// </summary>
    private void LearnDigits()
    {
        int idx = _live.SelectedIndex;
        if (idx < 0 || _reader.NumMaskAt(idx) is not { } mask)
        {
            MessageBox.Show(this, "위 인식 목록에서 줄을 먼저 선택하세요. ('?초' 줄이 학습 대상)", Text);
            return;
        }
        using var img = GlyphMatcher.ToBitmap(mask, scale: 6, pad: 6, fg: Theme.Header, bg: Theme.BgTertiary);
        using var dlg = new Form
        {
            Text = "숫자 학습", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font,
            ClientSize = new Size(Math.Max(300, img.Width + 24), img.Height + 110),
        };
        CaptureGuard.Attach(dlg);
        var pic = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.CenterImage, Dock = DockStyle.Top, Height = img.Height + 12 };
        var lbl = new Label { Text = "위 그림의 숫자(초)를 입력하세요", Dock = DockStyle.Top, Height = 24, Padding = new Padding(8, 4, 0, 0) };
        var num = new NumericUpDown { Minimum = 0, Maximum = 9999, Width = 90, Left = 10, Top = img.Height + 50 };
        var ok = new Button { Text = "학습", DialogResult = DialogResult.OK, Left = 110, Top = img.Height + 48, AutoSize = true };
        dlg.Controls.Add(num);
        dlg.Controls.Add(ok);
        dlg.Controls.Add(lbl);
        dlg.Controls.Add(pic);
        dlg.AcceptButton = ok;
        Theme.Apply(dlg);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        MessageBox.Show(this, _reader.LearnMask(mask, (int)num.Value).msg, Text);
    }

    private void AddRule(SkillRule r)
    {
        _rules.Add(r);
        _save();
        _grid.ClearSelection();
        _grid.CurrentCell = _grid.Rows[^1].Cells[1];
        _key.Focus(); // 바로 키를 누르면 지정되도록
    }

    private void RemoveRule()
    {
        if (Current() is not { } r) return;
        _rules.Remove(r);
        _save();
    }

    /// <summary>실시간 인식 결과 반영 (MainForm 타이머에서 호출)</summary>
    public void UpdateLive(List<CooldownLine>? lines, bool complete, Bitmap? preview, string learned)
    {
        if (!Visible) return;
        _liveStatus.Text = lines is null
            ? "애드온 창 찾는 중... (게임에서 스킬을 써서 쿨타임이 보이게 하세요)"
            : $"인식 중{(complete ? "" : " (일부 줄 판독 불가 → 숫자 학습 중)")}  ·  학습된 숫자 {learned}  ·  더블클릭 = 규칙 추가";

        // 선택 유지하며 목록 갱신 (같은 이름 기준)
        string? sel = (_live.SelectedItem as CooldownLine)?.Name;
        // 규칙에 등록된 스킬은 규칙 이름으로 표시
        var items = (lines ?? new()).Select(l =>
            _rules.FirstOrDefault(r => r.NameKey.Length > 0 && CooldownReader.NameSimilarity(r.NameKey, l.NameKey) >= 0.8) is { } rule
                ? l with { Name = rule.Name } : l).ToList();
        if (!items.SequenceEqual(_live.Items.Cast<CooldownLine>()))
        {
            _live.BeginUpdate();
            _live.Items.Clear();
            foreach (var l in items) _live.Items.Add(l);
            var keep = items.FirstOrDefault(l => l.Name == sel);
            if (keep is not null) _live.SelectedItem = keep;
            _live.EndUpdate();
        }
        if (preview is not null && !ReferenceEquals(_lastPreviewSrc, preview))
        {
            _lastPreviewSrc = preview;
            var old = _preview.Image;
            _preview.Image = new Bitmap(preview); // 원본은 인식기가 다음 회차에 해제하므로 복사본 표시
            old?.Dispose();
        }

        // 상태 열만 갱신 (편집 중이면 건너뜀)
        if (!_grid.IsCurrentCellInEditMode) _grid.InvalidateColumn(_grid.Columns.Count - 1);
    }
}
