// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ToyBoxDecryptor.Core;

namespace ToyBoxDecryptor.App;

public sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(17, 21, 28), Surface = Color.FromArgb(25, 31, 41),
        Raised = Color.FromArgb(35, 43, 56), Border = Color.FromArgb(49, 59, 73), TextColor = Color.FromArgb(233, 238, 247),
        Muted = Color.FromArgb(152, 166, 188), Accent = Color.FromArgb(91, 204, 190);
    private readonly DataGridView grid = new();
    private readonly RichTextBox preview = new(), activity = new();
    private readonly Label empty = new(), status = new(), previewCaption = new(), queueCount = new();
    private readonly TextBox output = new();
    private readonly ComboBox gameProfile = new();
    private readonly CheckBox textExport = new(), pcExport = new(), cmpExport = new(), toyBoxOnly = new();
    private readonly Button decrypt = new ThemeButton(), cancel = new ThemeButton(), openOutput = new ThemeButton();
    private readonly ProgressTrack progress = new();
    private readonly List<Control> idleControls = [];
    private readonly Dictionary<string, DataGridViewRow> rows = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cancellation;
    private bool busy;
    private int previewRequest;
    private string? lastExport;
    public BatchResult? LastResult { get; private set; }
    public string DefaultOutput { get; } = Path.Combine(AppContext.BaseDirectory, "Exports");

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    public MainForm()
    {
        SuspendLayout();
        Text = "LiteralToyBoxDecryptor — 1.0 / 3.0";
        ClientSize = new Size(1200, 830); MinimumSize = new Size(960, 650);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background; ForeColor = TextColor; Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        using (var bitmap = MakeIcon()) { Icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon()); }
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 18), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.Absolute, 92)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 75));
        Controls.Add(root);
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(Label("DISNEY INFINITY 1.0 + 3.0  /  SAVE UTILITIES", 10, Accent, new(0, 0), new(850, 22)));
        header.Controls.Add(Label("LiteralToyBoxDecryptor", 26, TextColor, new(0, 20), new(950, 45), FontStyle.Bold));
        header.Controls.Add(Label("DI 3.0 is experimental: console decryption and in-game compatibility have not been tested.", 10, Muted, new(1, 66), new(1060, 22)));
        root.Controls.Add(header, 0, 0);

        var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        workspace.ColumnStyles.Add(new(SizeType.Percent, 100)); workspace.ColumnStyles.Add(new(SizeType.Absolute, 284));
        root.Controls.Add(workspace, 0, 1);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0, 0, 18, 0) };
        left.RowStyles.Add(new(SizeType.Absolute, 48)); left.RowStyles.Add(new(SizeType.Percent, 100));
        left.RowStyles.Add(new(SizeType.Absolute, 32)); left.RowStyles.Add(new(SizeType.Absolute, 184));
        workspace.Controls.Add(left, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        AddButton(actions, "+ Add files", 112, async () => {
            using var dialog = new OpenFileDialog { Title = "Add DI 1.0 saves or ZIP packages", Filter = "Saves and ZIP packages|*.*", Multiselect = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) await AddPathsAsync(dialog.FileNames);
        });
        AddButton(actions, "+ Add folder", 122, async () => {
            using var dialog = new FolderBrowserDialog { Description = "Select a folder of extracted saves (subfolders are included)", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) await AddPathsAsync([dialog.SelectedPath]);
        });
        AddButton(actions, "Remove", 92, () => {
            foreach (DataGridViewRow row in grid.SelectedRows) { rows.Remove(((InputFile)row.Tag!).Key); grid.Rows.Remove(row); }
            UpdateQueue(); return Task.CompletedTask;
        });
        AddButton(actions, "Clear", 74, () => { rows.Clear(); grid.Rows.Clear(); preview.Clear(); UpdateQueue(); return Task.CompletedTask; });
        left.Controls.Add(actions, 0, 0);
        var queuePanel = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = Padding.Empty };
        queuePanel.Controls.Add(grid); left.Controls.Add(queuePanel, 0, 1);
        SetupGrid();
        empty.Dock = DockStyle.Fill; empty.Text = "Drop save files, folders or ZIPs here\n\nDI 1.0  /  DI 3.0 (experimental)\nPC  /  PS3  /  Wii U";
        empty.TextAlign = ContentAlignment.MiddleCenter; empty.ForeColor = Muted; empty.Font = new Font("Segoe UI", 12);
        empty.BackColor = Surface; queuePanel.Controls.Add(empty); empty.BringToFront();
        var caption = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        caption.ColumnStyles.Add(new(SizeType.Percent, 70)); caption.ColumnStyles.Add(new(SizeType.Percent, 30));
        previewCaption.Dock = DockStyle.Fill; previewCaption.Text = "Select a file to preview its decrypted text"; previewCaption.ForeColor = Muted;
        previewCaption.TextAlign = ContentAlignment.MiddleLeft; previewCaption.AutoEllipsis = true;
        queueCount.Dock = DockStyle.Fill; queueCount.TextAlign = ContentAlignment.MiddleRight; queueCount.ForeColor = Muted;
        caption.Controls.Add(previewCaption, 0, 0); caption.Controls.Add(queueCount, 1, 0); left.Controls.Add(caption, 0, 2);
        var textArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        textArea.RowStyles.Add(new(SizeType.Absolute, 37)); textArea.RowStyles.Add(new(SizeType.Percent, 100));
        var viewActions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var textTab = MakeButton("Text preview", 140); var logTab = MakeButton("Activity", 108);
        textTab.BackColor = Raised; logTab.BackColor = Background; viewActions.Controls.AddRange([textTab, logTab]);
        var textPanel = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(10), Margin = Padding.Empty };
        SetupText(preview); SetupText(activity); textPanel.Controls.AddRange([preview, activity]); activity.Visible = false;
        textTab.Click += (_, _) => { preview.Visible = true; activity.Visible = false; textTab.BackColor = Raised; logTab.BackColor = Background; };
        logTab.Click += (_, _) => { preview.Visible = false; activity.Visible = true; logTab.BackColor = Raised; textTab.BackColor = Background; };
        textArea.Controls.Add(viewActions, 0, 0); textArea.Controls.Add(textPanel, 0, 1); left.Controls.Add(textArea, 0, 3);

        var settings = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(18), Margin = Padding.Empty,
            AutoScroll = true, AutoScrollMinSize = new Size(0, 640) };
        workspace.Controls.Add(settings, 1, 0);
        settings.Controls.Add(Label("EXPORT SETTINGS", 10, Accent, new(18, 18), new(245, 24), FontStyle.Bold));
        settings.Controls.Add(Label("Output folder", 11, TextColor, new(18, 58), new(240, 25), FontStyle.Bold));
        output.SetBounds(18, 88, 247, 60); output.Multiline = true; output.ReadOnly = true; output.BorderStyle = BorderStyle.FixedSingle;
        output.BackColor = Raised; output.ForeColor = TextColor; output.Text = DefaultOutput; settings.Controls.Add(output);
        var browse = MakeButton("Choose folder…", 247); browse.SetBounds(18, 158, 247, 34);
        browse.Click += (_, _) => {
            using var dialog = new FolderBrowserDialog { Description = "Choose where to create a new export folder", UseDescriptionForTitle = true, SelectedPath = output.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.SelectedPath;
        };
        settings.Controls.Add(browse); idleControls.Add(browse);
        settings.Controls.Add(Label("Game profile", 11, TextColor, new(18, 210), new(240, 25), FontStyle.Bold));
        gameProfile.SetBounds(18, 238, 247, 32); gameProfile.DropDownStyle = ComboBoxStyle.DropDownList;
        gameProfile.BackColor = Raised; gameProfile.ForeColor = TextColor; gameProfile.FlatStyle = FlatStyle.Flat;
        gameProfile.Items.AddRange(["Auto (content markers)", "DI 1.0", "DI 3.0 (experimental)"]); gameProfile.SelectedIndex = 0;
        settings.Controls.Add(gameProfile); idleControls.Add(gameProfile);
        gameProfile.SelectedIndexChanged += async (_, _) => {
            previewRequest++;
            foreach (DataGridViewRow row in grid.Rows) {
                row.Cells[1].Value = "Auto-detect"; row.Cells[2].Value = "—"; row.Cells[3].Value = "—"; row.Cells[4].Value = "Ready";
                row.Cells[4].Style.ForeColor = TextColor; row.Cells[2].ToolTipText = ((InputFile)row.Tag!).Location;
                row.Cells[4].ToolTipText = ((InputFile)row.Tag!).Location;
            }
            await PreviewAsync();
        };
        settings.Controls.Add(Label("Formats", 11, TextColor, new(18, 292), new(240, 25), FontStyle.Bold));
        ConfigureCheck(textExport, "Readable text (.txt)", 325, true, settings);
        ConfigureCheck(pcExport, "Decrypted PC saves", 356, true, settings);
        ConfigureCheck(cmpExport, "Raw CMP1 blocks", 387, false, settings);
        ConfigureCheck(toyBoxOnly, "Toy Box files only", 435, true, settings);
        settings.Controls.Add(Label("Includes Toy Box / Game companions.\nSelect DI 3.0 for unmarked headers.\nTurn off to decode profiles or other saves.", 9, Muted, new(18, 472), new(246, 66)));
        var safety = Label("Every run creates a fresh export folder.\nSource files are never overwritten.\nA JSON report records every result.", 9, Muted, new(18, 550), new(246, 63));
        settings.Controls.Add(safety);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 15, 0, 0) };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 440)); root.Controls.Add(footer, 0, 2);
        var progressPanel = new Panel { Dock = DockStyle.Fill };
        status.SetBounds(0, 0, 615, 27); status.Text = "Ready. Add saves or drop a folder to begin."; status.ForeColor = Muted;
        status.AutoEllipsis = true; status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        progress.SetBounds(0, 34, 620, 7); progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        progress.Style = ProgressBarStyle.Continuous; progressPanel.Controls.AddRange([status, progress]); footer.Controls.Add(progressPanel, 0, 0);
        var bottomActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        StyleButton(decrypt, "Decrypt batch", 172, true); decrypt.Click += async (_, _) => await ExportAsync();
        StyleButton(cancel, "Cancel", 88); cancel.Enabled = false; cancel.Click += (_, _) => cancellation?.Cancel();
        StyleButton(openOutput, "Open output", 135); openOutput.Enabled = false; openOutput.Click += (_, _) => {
            if (lastExport != null) Process.Start(new ProcessStartInfo(lastExport) { UseShellExecute = true });
        };
        bottomActions.Controls.AddRange([decrypt, cancel, openOutput]); footer.Controls.Add(bottomActions, 1, 0);
        AcceptButton = decrypt; CancelButton = cancel;
        foreach (Control target in new Control[] { this, grid, empty }) {
            target.AllowDrop = true;
            target.DragEnter += (_, e) => e.Effect = !busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            target.DragDrop += async (_, e) => { if (!busy && e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await AddPathsAsync(paths); };
        }
        grid.SelectionChanged += async (_, _) => await PreviewAsync();
        FormClosing += (_, e) => { if (busy) { cancellation?.Cancel(); e.Cancel = true; status.Text = "Stopping safely… Close the window after the operation finishes."; } };
        UpdateQueue();
        ResumeLayout(true);
        PerformAutoScale();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int enabled = 1; _ = DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)); } catch (DllNotFoundException) { }
        ScaleGrid();
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Location.X < -10000) return; // Offscreen QA rendering uses the complete designed window.
        Rectangle work = Screen.FromControl(this).WorkingArea;
        if (Width > work.Width || Height > work.Height) {
            MinimumSize = new(Math.Min(MinimumSize.Width, work.Width), Math.Min(MinimumSize.Height, work.Height));
            Size = new(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
            Location = new(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
        }
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); ScaleGrid(); }
    private void ScaleGrid()
    {
        int height = 34 * DeviceDpi / 96;
        grid.RowTemplate.Height = height; grid.ColumnHeadersHeight = 37 * DeviceDpi / 96;
        foreach (DataGridViewRow row in grid.Rows) row.Height = height;
    }
    private void SetupGrid()
    {
        grid.Dock = DockStyle.Fill; grid.BackgroundColor = Surface; grid.BorderStyle = BorderStyle.None;
        grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = true;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Raised, ForeColor = Muted, Font = new Font("Segoe UI", 9, FontStyle.Bold), SelectionBackColor = Raised };
        grid.ColumnHeadersHeight = 37; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = TextColor, SelectionBackColor = Color.FromArgb(41, 68, 76), SelectionForeColor = TextColor, Padding = new Padding(5), Font = new Font("Segoe UI", 9) };
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(28, 35, 46); grid.GridColor = Border;
        grid.RowTemplate.Height = 34; grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        foreach (var spec in new[] { ("File", 35f), ("Platform", 16f), ("Game", 18f), ("Type", 20f), ("Status", 11f) }) {
            int index = grid.Columns.Add(spec.Item1, spec.Item1); grid.Columns[index].FillWeight = spec.Item2;
            grid.Columns[index].SortMode = DataGridViewColumnSortMode.NotSortable;
        }
    }
    private static Label Label(string text, float size, Color color, Point location, Size dimensions, FontStyle style = FontStyle.Regular) =>
        new() { Text = text, Font = new Font("Segoe UI", size, style), ForeColor = color, Location = location, Size = dimensions, AutoEllipsis = true };
    private static void SetupText(RichTextBox control)
    {
        control.Dock = DockStyle.Fill; control.BackColor = Surface; control.ForeColor = TextColor; control.ReadOnly = true;
        control.BorderStyle = BorderStyle.None; control.Font = new Font("Consolas", 9); control.WordWrap = false; control.DetectUrls = false;
    }
    private static Button MakeButton(string caption, int width) { var button = new ThemeButton(); StyleButton(button, caption, width); return button; }
    private static void StyleButton(Button button, string caption, int width, bool primary = false)
    {
        button.Text = caption; button.Size = new(width, 36); button.FlatStyle = FlatStyle.Flat;
        button.BackColor = primary ? Accent : Raised; button.ForeColor = primary ? Background : TextColor;
        button.FlatAppearance.BorderColor = primary ? Accent : Border; button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(126, 225, 212) : Color.FromArgb(47, 60, 78);
        button.Font = new Font("Segoe UI", 10, primary ? FontStyle.Bold : FontStyle.Regular); button.Cursor = Cursors.Hand;
        button.Margin = new Padding(0, 0, 8, 0); button.UseVisualStyleBackColor = false;
    }
    private sealed class ThemeButton : Button
    {
        public ThemeButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            bool hover = Enabled && ClientRectangle.Contains(PointToClient(MousePosition));
            bool primary = BackColor == Accent;
            Color fill = hover ? (primary ? Color.FromArgb(126, 225, 212) : Color.FromArgb(47, 60, 78)) : BackColor;
            if (!Enabled && primary) fill = Color.FromArgb(43, 83, 80);
            e.Graphics.Clear(fill);
            using var pen = new Pen(primary ? fill : Border); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(4, 4, Width - 8, Height - 8), ForeColor, fill);
        }
    }
    private sealed class ProgressTrack : Control
    {
        private readonly System.Windows.Forms.Timer animation = new() { Interval = 50 };
        private int value, maximum = 100, frame;
        private ProgressBarStyle style;
        public ProgressTrack()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            animation.Tick += (_, _) => { frame = (frame + 4) % 120; Invalidate(); };
        }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value { get => value; set { this.value = Math.Clamp(value, 0, maximum); Invalidate(); } }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Maximum { get => maximum; set { maximum = Math.Max(1, value); Invalidate(); } }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ProgressBarStyle Style { get => style; set { style = value; animation.Enabled = value == ProgressBarStyle.Marquee; Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Raised); using var brush = new SolidBrush(Accent);
            if (style == ProgressBarStyle.Marquee) e.Graphics.FillRectangle(brush, (int)(Width * (frame / 120f)), 0, Width / 5, Height);
            else e.Graphics.FillRectangle(brush, 0, 0, Width * value / maximum, Height);
        }
        protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
    }
    private void AddButton(Control parent, string caption, int width, Func<Task> action)
    {
        var button = MakeButton(caption, width); button.Click += async (_, _) => { if (!busy) await action(); };
        parent.Controls.Add(button); idleControls.Add(button);
    }
    private void ConfigureCheck(CheckBox check, string caption, int y, bool value, Control parent)
    {
        check.Text = caption; check.SetBounds(18, y, 247, 29); check.Checked = value; check.ForeColor = TextColor;
        parent.Controls.Add(check); idleControls.Add(check);
    }
    private static Bitmap MakeIcon()
    {
        var bitmap = new Bitmap(32, 32); using var g = Graphics.FromImage(bitmap);
        g.Clear(Background); using var pen = new Pen(Accent, 2);
        g.DrawPolygon(pen, [new Point(16, 3), new Point(28, 10), new Point(28, 24), new Point(16, 30), new Point(4, 24), new Point(4, 10)]);
        g.DrawLines(pen, [new Point(4, 10), new Point(16, 17), new Point(28, 10)]); g.DrawLine(pen, 16, 17, 16, 30);
        return bitmap;
    }
    private void SetBusy(bool value)
    {
        busy = value; foreach (Control control in idleControls) control.Enabled = !value;
        decrypt.Enabled = !value && rows.Count > 0; cancel.Enabled = value; openOutput.Enabled = !value && lastExport != null;
        UseWaitCursor = value; cancellation?.Dispose(); cancellation = value ? new() : null;
    }
    private void UpdateQueue()
    {
        empty.Visible = rows.Count == 0; grid.Visible = rows.Count > 0; queueCount.Text = $"{rows.Count:N0} file{(rows.Count == 1 ? "" : "s")}";
        decrypt.Enabled = !busy && rows.Count > 0;
    }
    private void Log(string message)
    {
        activity.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n"); activity.SelectionStart = activity.TextLength; activity.ScrollToCaret();
    }
    public async Task AddPathsAsync(string[] paths)
    {
        if (busy) return;
        SetBusy(true); status.Text = "Finding save files…"; progress.Style = ProgressBarStyle.Marquee;
        try {
            string excluded = output.Text;
            CancellationToken token = cancellation!.Token;
            var scan = await Task.Run(() => InputScanner.Scan(paths, token, excluded));
            int added = 0;
            grid.SuspendLayout();
            foreach (var file in scan.Files) {
                if (rows.ContainsKey(file.Key)) continue;
                if (rows.Count >= InputScanner.MaxInputs) { Log("Queue limit reached (20,000 files)."); break; }
                string displayName = Path.GetFileName(file.Name) + "   ·   " + Path.GetFileName(file.Package);
                var row = grid.Rows[grid.Rows.Add(displayName, "Auto-detect", "—", "—", "Ready")];
                row.Tag = file; foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = file.Location;
                rows.Add(file.Key, row); added++;
            }
            grid.ResumeLayout();
            foreach (string message in scan.Messages) Log(message);
            status.Text = $"Added {added:N0} saves. {scan.Ignored:N0} unrelated files ignored.";
            if (scan.Messages.Count > 0) status.Text += $" {scan.Messages.Count} scan issue(s); see Activity.";
            Log(status.Text);
            if (rows.Count == 0) preview.Text = "No extracted DI 1.0 / 3.0 saves were found.\n\nSupply extracted save files or a ZIP of them.\nPS3 system containers must already be extracted/decrypted by your save tool.";
        }
        catch (OperationCanceledException) { status.Text = "Scan cancelled."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { status.Text = "Could not scan the input. See Activity."; Log(e.Message); }
        finally { progress.Style = ProgressBarStyle.Continuous; progress.Value = 0; SetBusy(false); UpdateQueue(); }
        if (grid.Rows.Count > 0 && grid.SelectedRows.Count == 0) { grid.CurrentCell = grid.Rows[0].Cells[0]; grid.Rows[0].Selected = true; }
        await PreviewAsync();
    }
    private async Task PreviewAsync()
    {
        int request = ++previewRequest;
        if (grid.SelectedRows.Count != 1) { previewCaption.Text = "Select one file to preview its decrypted text"; preview.Clear(); return; }
        if (grid.SelectedRows[0].Tag is not InputFile input) return;
        previewCaption.Text = "Reading " + Path.GetFileName(input.Name) + "…";
        try {
            GameProfile selected = SelectedProfile;
            var decoded = await Task.Run(() => SaveCodec.Decode(input.Read(), selected, input.Name));
            if (request != previewRequest || IsDisposed) return;
            string text = decoded.Text;
            preview.Text = text.Length > 160000 ? text[..160000] + "\n\n[Preview truncated. Export contains the complete text.]" : text;
            previewCaption.Text = decoded.GameLabel + " · " + decoded.Platform + " · Checksums verified";
            if (rows.TryGetValue(input.Key, out var row)) {
                row.Cells[1].Value = decoded.Platform; row.Cells[2].Value = decoded.GameLabel; row.Cells[3].Value = decoded.KindLabel;
                row.Cells[2].ToolTipText = decoded.VersionEvidence + " " + decoded.CompatibilityNote;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
            if (request != previewRequest || IsDisposed) return;
            previewCaption.Text = "Preview unavailable"; preview.Text = e.Message;
        }
    }
    private async Task ExportAsync()
    {
        if (busy || rows.Count == 0) return;
        if (!textExport.Checked && !pcExport.Checked && !cmpExport.Checked) { status.Text = "Select at least one export format."; return; }
        var inputs = grid.Rows.Cast<DataGridViewRow>().Select(r => (InputFile)r.Tag!).ToList();
        var options = new ExportOptions(textExport.Checked, pcExport.Checked, cmpExport.Checked, toyBoxOnly.Checked, SelectedProfile);
        string root = output.Text;
        SetBusy(true); status.Text = "Verifying and exporting…"; progress.Maximum = inputs.Count; progress.Value = 0;
        var notification = new Progress<BatchProgress>(p => {
            progress.Value = p.Completed; status.Text = $"{p.Completed:N0} / {p.Total:N0} · {Path.GetFileName(p.Input.Name)}";
            if (rows.TryGetValue(p.Input.Key, out var row)) {
                row.Cells[1].Value = p.Result.Platform; row.Cells[2].Value = p.Result.Game; row.Cells[3].Value = p.Result.Type; row.Cells[4].Value = p.Result.Status;
                row.Cells[4].Style.ForeColor = p.Result.Status == "Failed" ? Color.FromArgb(255, 142, 149) : p.Result.Status == "Saved" ? Accent : Muted;
                row.Cells[2].ToolTipText = p.Result.VersionEvidence + " " + p.Result.Compatibility;
                row.Cells[4].ToolTipText = p.Result.Message + " " + p.Result.Compatibility;
            }
            if (p.Result.Status != "Saved") Log(p.Input.Name + ": " + p.Result.Message);
        });
        try {
            LastResult = await Task.Run(() => BatchExporter.Run(inputs, root, options, notification, cancellation!.Token));
            lastExport = LastResult.Directory;
            status.Text = $"{(LastResult.Cancelled ? "Cancelled" : "Complete")}: {LastResult.Saved:N0} saved · {LastResult.Skipped:N0} skipped · {LastResult.Failed:N0} failed";
            Log(status.Text); Log("Output: " + lastExport);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { status.Text = "Export could not finish. See Activity."; Log(e.Message); }
        finally { SetBusy(false); }
    }
    private GameProfile SelectedProfile => gameProfile.SelectedIndex switch { 1 => GameProfile.DI1, 2 => GameProfile.DI3, _ => GameProfile.Auto };
    internal async Task SmokeAsync(string source, string destination, GameProfile profile = GameProfile.Auto)
    {
        output.Text = destination; cmpExport.Checked = true;
        gameProfile.SelectedIndex = profile switch { GameProfile.DI1 => 1, GameProfile.DI3 => 2, _ => 0 };
        await AddPathsAsync([source]);
        if (rows.Count == 0) throw new InvalidOperationException("UI scanner found no files.");
        await ExportAsync();
        if (LastResult == null || LastResult.Saved == 0 || LastResult.Failed > 0) throw new InvalidOperationException("UI batch did not finish successfully.");
        using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        bitmap.Save(Path.Combine(destination, "ui-batch.png"), System.Drawing.Imaging.ImageFormat.Png);
        File.WriteAllText(Path.Combine(destination, "ui-smoke.txt"), $"PASS: {LastResult.Saved} saves exported through the GUI.\n{status.Text}\n{lastExport}");
    }
}
