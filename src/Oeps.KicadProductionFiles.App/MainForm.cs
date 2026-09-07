using System.Reflection;
using System.Text;
using Oeps.KicadProductionFiles.Core.Checks;
using Oeps.KicadProductionFiles.Core.CheckFilesConfiguration;
using Oeps.KicadProductionFiles.Core.Configuration;
using Oeps.KicadProductionFiles.Core.ConfigureFiles;
using Oeps.KicadProductionFiles.Core.GenerateProductionFiles;
using Oeps.KicadProductionFiles.Core.Data;
using Oeps.KicadProductionFiles.Core.Updates;

namespace Oeps.KicadProductionFiles.App;

public sealed class MainForm : Form
{
    private static readonly Color Muted = Color.FromArgb(87, 99, 114);
    private static readonly Color Accent = Color.FromArgb(0, 103, 192);
    private readonly AppConfiguration _config;
    private readonly AppPaths _paths;
    private readonly UserSettings _settings;
    private readonly ComponentRepository _repository;
    private readonly HttpClient _http;
    private readonly string[] _args;
    private readonly CancellationTokenSource _closing = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 20000 };
    private readonly TextBox _cli = PathBox("KiCad CLI path", "Select kicad-cli.exe");
    private readonly TextBox _project = PathBox("KiCad files path", "Select the project folder");
    private readonly TextBox _revision = PathBox("Revision", "revB");
    private readonly Button _browseCli = BrowseButton("Browse for KiCad CLI");
    private readonly Button _browseProject = BrowseButton("Browse for KiCad files");
    private readonly Button _refresh = ActionButton("&Update database");
    private readonly Button _check = ActionButton("&Check files configuration");
    private readonly Button _configure = ActionButton("Con&figure files");
    private readonly Button _generate = ActionButton("&Generate production files", primary: true);
    private readonly CheckBox _generateWithErrors = GenerationOption("Generate production files even with errors", false);
    private readonly CheckBox _deleteBeforeGeneration = GenerationOption("Delete all files before generate production files", true);
    private readonly CheckBox _generateGerbers = GenerationOption("Generate Gerber files", true);
    private readonly CheckBox _generatePlacements = GenerationOption("Generate placement files", true);
    private readonly CheckBox _generateDrills = GenerationOption("Generate drill files", true);
    private readonly CheckBox _generateIpcD356 = GenerationOption("Generate IPC-D-356 netlist", true);
    private readonly TextBox _report = new()
    {
        Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Color.FromArgb(32, 44, 58),
        AccessibleName = "Check report", WordWrap = true, Margin = new Padding(0, 8, 0, 0)
    };
    private readonly Label _jobStatus = new() { Dock = DockStyle.Fill, ForeColor = Muted, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _syncStatus = new() { Dock = DockStyle.Fill, ForeColor = Muted, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _syncIndicator = new() { Text = "●", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly LinkLabel _updateStatus = new() { AutoSize = true, LinkColor = Color.FromArgb(28, 95, 171) };
    private readonly string _version = (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0").Split('+')[0];
    private bool _checking, _configuring, _generating, _refreshing, _checkingRelease;
    private bool _checkRequested, _configurationPassed;
    private int _reportRevision;
    private DateTimeOffset _nextReleaseCheck = DateTimeOffset.MinValue;

    public MainForm(AppConfiguration config, AppPaths paths, HttpClient http, string[] args)
    {
        SuspendLayout();
        _config = config; _paths = paths; _http = http; _args = args;
        _settings = UserSettings.Load(paths.SettingsFile);
        _repository = new ComponentRepository(http, config.SpreadsheetCsvUrl,
            config.SampleMode ? Path.Combine(AppContext.BaseDirectory, "samples", "components.csv") : paths.CacheCsvFile,
            config.HeaderAliases);
        _repository.LoadCache();
        Text = "OEPS KiCad Production Files";
        using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Oeps.AppIcon"))
        {
            if (stream is not null) { using var icon = new Icon(stream); Icon = (Icon)icon.Clone(); }
        }
        if (args.Contains("--ui-smoke")) { ShowInTaskbar = false; Opacity = 0; }
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.FromArgb(248, 250, 252);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(_settings.WindowWidth, Math.Max(720, _settings.WindowHeight));
        MinimumSize = SizeFromClientSize(new Size(560, 720));
        StartPosition = FormStartPosition.CenterScreen;
        if (_settings.WindowX is int x && _settings.WindowY is int y && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(x, y, 100, 100))))
        { StartPosition = FormStartPosition.Manual; Location = new Point(x, y); }
        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
        BuildLayout();
        _cli.Text = string.IsNullOrWhiteSpace(_settings.KicadCliPath) ? DetectCli() : _settings.KicadCliPath;
        _project.Text = _settings.ProjectDirectory;
        _revision.Text = _settings.Revision;
        ShowReadyReport();
        _jobStatus.Text = _settings.LoadError ?? "Choose a project folder to get started.";
        _browseCli.Click += (_, _) => BrowseCli();
        _browseProject.Click += (_, _) => BrowseProject();
        _cli.TextChanged += (_, _) => InputsChanged();
        _project.TextChanged += (_, _) => InputsChanged();
        _revision.TextChanged += (_, _) => InputsChanged();
        _cli.Leave += (_, _) => SavePaths();
        _project.Leave += (_, _) => SavePaths();
        _revision.Leave += (_, _) => SavePaths();
        _refresh.Click += async (_, _) => await RefreshAsync(manual: true);
        _check.Click += async (_, _) => await CheckAsync();
        _configure.Click += async (_, _) => await ConfigureAsync();
        _generate.Click += async (_, _) => await GenerateAsync();
        _generateWithErrors.CheckedChanged += (_, _) => UpdateButtons();
        _generateGerbers.CheckedChanged += (_, _) => UpdateButtons();
        _generatePlacements.CheckedChanged += (_, _) => UpdateButtons();
        _generateDrills.CheckedChanged += (_, _) => UpdateButtons();
        _generateIpcD356.CheckedChanged += (_, _) => UpdateButtons();
        _updateStatus.LinkClicked += (_, _) => _jobStatus.Text = "Close the app, then use the OEPS desktop shortcut to install the update.";
        _timer.Tick += async (_, _) =>
        {
            UpdateFooter();
            if (!_config.SampleMode && !_refreshing && _repository.IsRefreshDue) await RefreshAsync();
            if (!_checkingRelease && DateTimeOffset.UtcNow >= _nextReleaseCheck) await CheckReleaseAsync();
        };
        Shown += async (_, _) =>
        {
            AppInstanceCoordinator.SignalReadyFromArguments(args);
            _project.Focus();
            if (args.Contains("--ui-smoke")) { await RunUiSmokeAsync(); return; }
            _timer.Start();
            await Task.WhenAll(_repository.IsRefreshDue ? RefreshAsync() : Task.CompletedTask, CheckReleaseAsync());
        };
        FormClosing += OnClosing;
        UpdateFooter(); UpdateButtons();
        ResumeLayout(performLayout: true);
    }

    private static CheckBox GenerationOption(string text, bool initialValue) => new()
    {
        Text = text, AccessibleName = text, Checked = initialValue, AutoSize = true,
        Anchor = AnchorStyles.Right, CheckAlign = ContentAlignment.MiddleRight, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 3, 0, 3)
    };

    private static TextBox PathBox(string name, string placeholder) => new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, AccessibleName = name,
        PlaceholderText = placeholder, BackColor = Color.White
    };

    private static Button BrowseButton(string name) => new()
    {
        Text = "…", AccessibleName = name, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(239, 243, 247), Margin = new Padding(6, 4, 0, 4)
    };

    private static Button ActionButton(string text, bool primary = false)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(0, 93, 174) };
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        return button;
    }

    private static void DrawBorder(Panel panel) => panel.Paint += (_, e) =>
        ControlPaint.DrawBorder(e.Graphics, panel.ClientRectangle, Color.FromArgb(219, 226, 234), ButtonBorderStyle.Solid);

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
            Padding = new Padding(16, 12, 16, 10), Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 156, 12, 88, 186, 30 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3,
            BackColor = Color.White, Padding = new Padding(12), Margin = Padding.Empty };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        for (var row = 0; row < 3; row++) fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
        DrawBorder(fields);
        void Field(string caption, TextBox input, Button browse, int row)
        {
            var label = new Label { Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 8, 0), TabIndex = row * 3 };
            var frame = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(9, 7, 9, 5), Margin = new Padding(0, 4, 0, 4), TabIndex = row * 3 + 1 };
            frame.Paint += (_, e) => ControlPaint.DrawBorder(e.Graphics, frame.ClientRectangle, Color.FromArgb(199, 207, 217), ButtonBorderStyle.Solid);
            frame.Controls.Add(input); browse.TabIndex = row * 3 + 2;
            browse.FlatAppearance.BorderColor = Color.FromArgb(199, 207, 217);
            fields.Controls.Add(label, 0, row); fields.Controls.Add(frame, 1, row); fields.Controls.Add(browse, 2, row);
        }
        Field("KiCad C&LI", _cli, _browseCli, 0); Field("KiCad &files", _project, _browseProject, 1);
        fields.Controls.Add(new Label { Text = "&Revision", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 0), TabIndex = 6 }, 0, 2);
        var revisionRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, TabIndex = 7 };
        revisionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        revisionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        revisionRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var revisionFrame = new Panel { Dock = DockStyle.Fill, BackColor = Color.White,
            Padding = new Padding(9, 7, 9, 5), Margin = new Padding(0, 4, 0, 4) };
        revisionFrame.Paint += (_, e) => ControlPaint.DrawBorder(e.Graphics, revisionFrame.ClientRectangle,
            Color.FromArgb(199, 207, 217), ButtonBorderStyle.Solid);
        revisionFrame.Controls.Add(_revision);
        revisionRow.Controls.Add(revisionFrame, 0, 0);
        revisionRow.Controls.Add(new Label { Text = "e.g. revA, RevB, B", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, Margin = new Padding(10, 0, 0, 0) }, 1, 0);
        fields.Controls.Add(revisionRow, 1, 2); fields.SetColumnSpan(revisionRow, 2);
        layout.Controls.Add(fields, 0, 0);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty, TabIndex = 1 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _refresh.Margin = new Padding(0, 0, 4, 4); _check.Margin = new Padding(4, 0, 0, 4);
        _configure.Margin = new Padding(0, 4, 4, 0); _generate.Margin = new Padding(4, 4, 0, 0);
        _refresh.TabIndex = 0; _check.TabIndex = 1; _configure.TabIndex = 2; _generate.TabIndex = 3;
        _generate.Font = new Font(Font, FontStyle.Bold);
        buttons.Controls.Add(_refresh, 0, 0); buttons.Controls.Add(_check, 1, 0);
        buttons.Controls.Add(_configure, 0, 1); buttons.Controls.Add(_generate, 1, 1);
        layout.Controls.Add(buttons, 0, 2);
        var generationOptions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Margin = new Padding(0, 8, 0, 0), TabIndex = 2 };
        generationOptions.Controls.AddRange([_generateWithErrors, _deleteBeforeGeneration, _generateGerbers, _generatePlacements, _generateDrills, _generateIpcD356]);
        layout.Controls.Add(generationOptions, 0, 3);
        _jobStatus.Font = new Font(Font.FontFamily, 8.5f); layout.Controls.Add(_jobStatus, 0, 4);
        var reportFrame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            BackColor = Color.White, Padding = new Padding(12), Margin = Padding.Empty, TabIndex = 3 };
        reportFrame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        reportFrame.RowStyles.Add(new RowStyle(SizeType.Absolute, 23)); reportFrame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        DrawBorder(reportFrame);
        reportFrame.Controls.Add(new Label { Text = "Report", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), Margin = Padding.Empty }, 0, 0);
        reportFrame.Controls.Add(_report, 0, 1); layout.Controls.Add(reportFrame, 0, 5);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 35, ColumnCount = 4,
            Padding = new Padding(16, 0, 12, 0), BackColor = Color.FromArgb(240, 244, 248), Font = new Font(Font.FontFamily, 8.5f) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 19)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_syncIndicator, 0, 0); footer.Controls.Add(_syncStatus, 1, 0);
        footer.Controls.Add(new Label { Text = $"│  v{_version}", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        _updateStatus.Anchor = AnchorStyles.Right; footer.Controls.Add(_updateStatus, 3, 0);
        Controls.Add(layout); Controls.Add(footer);
        _toolTip.SetToolTip(_cli, "Path to kicad-cli.exe. An installed KiCad CLI is detected automatically when available.");
        _toolTip.SetToolTip(_project, "Project folder containing the .kicad_pro settings, main .kicad_sch schematic and .kicad_pcb board.");
        _toolTip.SetToolTip(_revision, "Expected revision. B, RevB and revB are equivalent; surrounding spaces are ignored.");
        _toolTip.SetToolTip(_check, "Check the saved Symbol Fields Table fields, Edit tab metadata, export configuration, field order, revision and PCB Gerber plot settings.");
        _toolTip.SetToolTip(_configure, "Recheck the project and confirm each failed configuration fix separately.");
        _toolTip.SetToolTip(_generate, "Generate the selected production files using KiCad CLI.");
        _toolTip.SetToolTip(_deleteBeforeGeneration, "When selected, clear the project's manufacturing folder first. When clear, keep unrelated files and replace matching generated filenames.");
    }

    private static string DetectCli()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KiCad");
            if (!Directory.Exists(root)) return "";
            return Directory.EnumerateDirectories(root)
                .OrderByDescending(path => Version.TryParse(Path.GetFileName(path), out var version) ? version : new Version())
                .Select(path => Path.Combine(path, "bin", "kicad-cli.exe")).FirstOrDefault(File.Exists) ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private void BrowseCli()
    {
        using var dialog = new OpenFileDialog { Title = "Select KiCad CLI", Filter = "KiCad CLI (kicad-cli.exe)|kicad-cli.exe|Executables (*.exe)|*.exe", CheckFileExists = true };
        if (File.Exists(_cli.Text)) dialog.FileName = _cli.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) { _cli.Text = dialog.FileName; SavePaths(); }
    }

    private void BrowseProject()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select the folder containing the KiCad project (.kicad_pro)", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (Directory.Exists(_project.Text)) dialog.SelectedPath = _project.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) { _project.Text = dialog.SelectedPath; SavePaths(); }
    }

    private void InputsChanged()
    {
        _checkRequested = _configurationPassed = false;
        UpdateButtons();
        _jobStatus.Text = "Inputs changed. Check the configuration again to update the report.";
        ShowReport("Inputs changed", "Click Check files configuration to create a report for the current project folder and revision.");
    }

    private void SavePaths()
    {
        _settings.KicadCliPath = _cli.Text.Trim().Trim('"');
        _settings.ProjectDirectory = _project.Text.Trim().Trim('"');
        _settings.Revision = _revision.Text.Trim();
        try { _settings.Save(_paths.SettingsFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _jobStatus.Text = "Could not save preferences: " + ex.Message; _toolTip.SetToolTip(_jobStatus, _jobStatus.Text); }
    }

    private void ShowReport(string title, string detail)
    {
        _reportRevision++;
        _report.Text = (title + "\n\n" + detail).ReplaceLineEndings("\r\n");
        _report.SelectionStart = 0; _report.SelectionLength = 0; _report.ScrollToCaret();
    }

    private void ShowReadyReport() => ShowReport("Ready",
        "Select the folder containing your KiCad project and enter the expected revision.\n\nCheck files configuration verifies the Symbol Fields Table fields, Edit tab metadata, export configuration, field order, revision and PCB Gerber plot settings.\n\nSave any changes in KiCad before running the check.");

    private async Task RefreshAsync(bool manual = false)
    {
        if (_refreshing || _closing.IsCancellationRequested) return;
        if (_config.SampleMode)
        {
            if (manual) ShowReport("Sample database", $"{_repository.Components.Count} sample PN/MPN pairings loaded. Start without --sample to refresh the shared spreadsheet.");
            return;
        }
        var reportRevision = _reportRevision;
        _refreshing = true; UpdateButtons(); UpdateFooter();
        try
        {
            await _repository.RefreshAsync(_closing.Token);
            if (_closing.IsCancellationRequested) return;
            if (manual && reportRevision == _reportRevision)
            {
                ShowReport(_repository.LastError is null ? "Database updated" : "Database refresh failed",
                    $"{_repository.Components.Count} OEPS PN/MPN pairings available.\r\n\r\nCSV cache: {_paths.CacheCsvFile}\r\n\r\nAutomatic refresh: every 5 minutes." +
                    (_repository.LastError is null ? "" : $"\r\n\r\n{_repository.LastError}\r\n\r\n" + (_repository.HasData ? "The last valid cache is still available." : "No valid database is available yet.")));
                _jobStatus.Text = _repository.LastError is null ? "Database refreshed." : "Database refresh failed. See report.";
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        finally { _refreshing = false; if (!IsDisposed && !_closing.IsCancellationRequested) { UpdateButtons(); UpdateFooter(); } }
    }

    private CheckContext CreateCheckContext() => new(_settings.KicadCliPath, _settings.ProjectDirectory,
        Revision: _settings.Revision);

    private async Task CheckAsync()
    {
        if (_checking || _configuring || _generating || _closing.IsCancellationRequested) return;
        SavePaths(); _checking = true;
        _checkRequested = true; _configurationPassed = false;
        UpdateButtons();
        _jobStatus.Text = "Checking files configuration…";
        ShowReport("Checking files configuration…", "Checking the saved Symbol Fields Table settings.");
        var context = CreateCheckContext();
        try
        {
            var report = await new ConfigurationCheckRunner().RunAsync(context, _closing.Token);
            if (_closing.IsCancellationRequested) return;
            _configurationPassed = report.Entries.Count > 0 && !report.HasFailures && !report.HasPendingChecks;
            ShowCheckReport(report);
            _jobStatus.Text = report.HasFailures ? "Configuration needs attention. See report." : "Configuration checks passed. Everything is good.";
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_closing.IsCancellationRequested) return;
            ShowReport("Check failed", ex.Message); _jobStatus.Text = "Check could not finish. See report.";
        }
        finally { _checking = false; if (!IsDisposed && !_closing.IsCancellationRequested) UpdateButtons(); }
    }

    private void ShowCheckReport(CheckReport report, IReadOnlyList<FixActionResult>? actions = null)
    {
        var text = new StringBuilder();
        foreach (var entry in report.Entries)
        {
            text.AppendLine($"[{entry.Status.ToString().ToUpperInvariant()}] {entry.Name}");
            if (entry.Status != CheckStatus.Passed) text.AppendLine(entry.Detail);
            text.AppendLine();
        }
        if (actions is { Count: > 0 })
        {
            text.AppendLine("Configuration actions");
            foreach (var action in actions)
            {
                var status = action.Status == FixActionStatus.Failed ? "FIX FAILED" : action.Status.ToString().ToUpperInvariant();
                text.AppendLine($"[{status}] {action.CheckName}");
                if (action.Status == FixActionStatus.Failed) text.AppendLine(action.Detail);
            }
            if (actions.Any(action => action.Status == FixActionStatus.Fixed))
                text.AppendLine("Original files were saved in .oeps-backups.");
        }
        ShowReport(report.Summary, text.ToString());
    }

    private async Task ConfigureAsync(Func<ConfigurationFixPrompt, CancellationToken, Task<bool>>? confirmAsync = null)
    {
        if (_checking || _configuring || _generating || _closing.IsCancellationRequested) return;
        SavePaths();
        _configuring = true; _checkRequested = true; _configurationPassed = false;
        UpdateButtons();
        ShowReport("Configure files", "Checking the saved configuration before preparing fixes…");
        _jobStatus.Text = "Checking and configuring files…";
        try
        {
            var result = await new ConfigurationFixRunner().RunAsync(
                CreateCheckContext(), confirmAsync ?? ConfirmFixAsync, _closing.Token);
            if (_closing.IsCancellationRequested) return;
            _configurationPassed = result.Checks.Entries.Count > 0 && !result.Checks.HasFailures && !result.Checks.HasPendingChecks;
            ShowCheckReport(result.Checks, result.Actions);
            _jobStatus.Text = result.Actions.Count == 0 ? "All checks passed. No fixes needed." :
                $"Configuration finished: {result.Actions.Count(action => action.Status == FixActionStatus.Fixed)} fixed, " +
                $"{result.Actions.Count(action => action.Status == FixActionStatus.Skipped)} skipped, " +
                $"{result.Actions.Count(action => action.Status == FixActionStatus.Failed)} could not be fixed.";
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_closing.IsCancellationRequested) return;
            ShowReport("Configuration could not finish", ex.Message);
            _jobStatus.Text = "Configuration could not finish. Run Check files configuration again.";
        }
        finally { _configuring = false; if (!IsDisposed && !_closing.IsCancellationRequested) UpdateButtons(); }
    }

    private Task<bool> ConfirmFixAsync(ConfigurationFixPrompt prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var answer = MessageBox.Show(this,
            $"[FAILED] {prompt.Failure.Name}\n\n{prompt.Failure.Detail}\n\nProposed fix:\n{prompt.Description}\n\nFix this configuration now?",
            "Configure files — " + prompt.Failure.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        return Task.FromResult(answer == DialogResult.Yes);
    }

    private async Task GenerateAsync(Func<bool>? confirmGerbers = null)
    {
        if (_checking || _configuring || _generating || _closing.IsCancellationRequested) return;
        var options = new ProductionGenerationOptions(_deleteBeforeGeneration.Checked, _generateGerbers.Checked,
            _generatePlacements.Checked, _generateDrills.Checked, _generateIpcD356.Checked);
        if (!options.GenerateGerbers && !options.GeneratePlacements && !options.GenerateDrills && !options.GenerateIpcD356)
        { _jobStatus.Text = "Select at least one file type to generate."; return; }
        if (options.GenerateGerbers && !(confirmGerbers ?? ConfirmGerberGeneration)())
        { _jobStatus.Text = "Generation cancelled. No production files were changed."; return; }
        SavePaths();
        _generating = true;
        UpdateButtons();
        ShowReport("Generate production files", "Preparing the selected exports…");
        _jobStatus.Text = "Preparing production export…";
        var progress = new Progress<string>(message =>
        {
            if (!IsDisposed && !_closing.IsCancellationRequested && _generating) _jobStatus.Text = message;
        });
        try
        {
            var context = CreateCheckContext();
            var result = await Task.Run(() => new ProductionGenerationRunner().RunAsync(context, progress, _closing.Token, options), _closing.Token);
            if (_closing.IsCancellationRequested) return;
            var report = new StringBuilder();
            if (result.ManufacturingCleared) report.AppendLine("Manufacturing folder cleared.").AppendLine();
            foreach (var file in result.Files)
            {
                report.AppendLine($"[GENERATED] {file.Name}");
                foreach (var path in file.RelativePaths) report.AppendLine(path);
                if (file.ComponentCount is int componentCount) report.AppendLine($"Component count: {componentCount}");
                report.AppendLine();
            }
            if (!result.Success) report.AppendLine("[FAILED] Production generation").AppendLine(result.Error);
            ShowReport(result.Success ? "Production files generated" : "Production generation failed", report.ToString());
            _jobStatus.Text = result.Success ? "Selected production files generated successfully." : "Generation failed. See report.";
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_closing.IsCancellationRequested) return;
            ShowReport("Production generation failed", ex.Message);
            _jobStatus.Text = "Generation failed. See report.";
        }
        finally { _generating = false; if (!IsDisposed && !_closing.IsCancellationRequested) UpdateButtons(); }
    }

    private bool ConfirmGerberGeneration() => MessageBox.Show(this,
        "Before generating the Gerber files, open the board and:\n\n" +
        "• Make sure that the zones were filled\n• Validate the Include Layers\n\nSave the board, then click OK to generate the files.",
        "Before generating Gerber files", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;

    private void UpdateButtons()
    {
        _refresh.Enabled = !_refreshing && !_checking && !_configuring && !_generating;
        _check.Enabled = !_checking && !_configuring && !_generating;
        _configure.Enabled = _checkRequested && !_configuring && !_generating;
        _generate.Enabled = !_checking && !_configuring && !_generating && (_generateWithErrors.Checked || _configurationPassed)
            && (_generateGerbers.Checked || _generatePlacements.Checked || _generateDrills.Checked || _generateIpcD356.Checked);
        foreach (var option in new[] { _generateWithErrors, _deleteBeforeGeneration, _generateGerbers, _generatePlacements, _generateDrills, _generateIpcD356 })
            option.Enabled = !_checking && !_configuring && !_generating;
        _generate.BackColor = _generate.Enabled ? Accent : Color.FromArgb(224, 230, 237);
        _cli.Enabled = _project.Enabled = _revision.Enabled = _browseCli.Enabled = _browseProject.Enabled = !_checking && !_configuring && !_generating;
    }

    private void UpdateFooter()
    {
        if (_config.SampleMode)
        { _syncIndicator.ForeColor = Color.FromArgb(219, 144, 14); _syncStatus.Text = "Sample database · offline demo"; return; }
        var remaining = Math.Max(0, (int)(_repository.NextRefreshUtc - DateTimeOffset.UtcNow).TotalSeconds);
        var next = _refreshing ? "refreshing…" : $"next in {remaining / 60:00}:{remaining % 60:00}";
        var last = _repository.LastSuccessfulSyncUtc;
        _syncIndicator.ForeColor = _repository.LastError is not null ? Color.FromArgb(219, 144, 14) : last.HasValue ? Color.FromArgb(39, 160, 80) : Color.Gray;
        _syncStatus.Text = _repository.LastError is not null ? $"Refresh failed · {(_repository.HasData ? "using cache" : "no cache")} · {next}"
            : last.HasValue ? $"Database synced {last.Value.LocalDateTime:HH:mm} · {next}" : $"Database not synced · {next}";
        _toolTip.SetToolTip(_syncStatus, $"{_repository.Components.Count} PN/MPN pairings.\nCSV: {_paths.CacheCsvFile}\nLast sync: {(last.HasValue ? last.Value.LocalDateTime.ToString("g") : "never")}" +
            (_repository.LastError is null ? "" : "\n" + _repository.LastError));
    }

    private async Task CheckReleaseAsync()
    {
        if (_checkingRelease || _config.SampleMode || _closing.IsCancellationRequested) return;
        _checkingRelease = true; _nextReleaseCheck = DateTimeOffset.UtcNow.AddMinutes(30);
        try
        {
            var release = await new GitHubReleaseClient(_http, _config.GitHubOwner, _config.GitHubRepository, _config.PackagePrefix).GetLatestReleaseAsync(_closing.Token);
            if (!_closing.IsCancellationRequested && release is not null && SemanticVersion.TryParse(_version, out var installed) && release.Version.CompareTo(installed) > 0)
            { _updateStatus.Text = "Update available"; _toolTip.SetToolTip(_updateStatus, "Close the app, then use its desktop shortcut to install the update."); }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        { /* An optional release lookup must not interrupt work. */ }
        finally { _checkingRelease = false; }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        _timer.Stop(); _closing.Cancel();
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var scale = DeviceDpi / 96f;
        _settings.WindowWidth = (int)((bounds.Width - (Width - ClientSize.Width)) / scale);
        _settings.WindowHeight = (int)((bounds.Height - (Height - ClientSize.Height)) / scale);
        _settings.WindowX = bounds.X; _settings.WindowY = bounds.Y; _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        SavePaths();
    }

    private async Task RunUiSmokeAsync()
    {
        var results = new List<string>();
        void ExpectButtons(bool configure, bool generate, string scenario)
        {
            if (_configure.Enabled != configure || _generate.Enabled != generate)
                throw new Exception($"Unexpected action availability {scenario}: Configure={_configure.Enabled}, Generate={_generate.Enabled}.");
        }
        try
        {
            if (!_repository.HasData) throw new Exception("Sample database did not load.");
            if (Icon is null || _generate.Top <= _check.Top || _check.Left <= _refresh.Left) throw new Exception("Icon or 2x2 action layout is missing.");
            if (_revision.PointToScreen(Point.Empty).Y <= _project.PointToScreen(Point.Empty).Y)
                throw new Exception("Revision entry must be below the KiCad files entry.");
            results.Add("PASS matching icon, two path fields, revision entry and 2x2 actions");
            ExpectButtons(false, false, "at startup");
            if (!_refresh.Enabled || !_check.Enabled || _generateWithErrors.Checked)
                throw new Exception("Startup buttons or default override are incorrect.");
            if (!_deleteBeforeGeneration.Checked || !_generateGerbers.Checked || !_generatePlacements.Checked || !_generateDrills.Checked || !_generateIpcD356.Checked)
                throw new Exception("Generation selections must default to true.");
            if (_generateWithErrors.Parent!.Right < ClientSize.Width - 40 || _generateDrills.Top <= _generatePlacements.Top
                || _generateWithErrors.Parent.Bottom > _jobStatus.Top)
                throw new Exception("Generation options must be stacked on the right without overlapping the report.");
            foreach (var option in new[] { _generateWithErrors, _deleteBeforeGeneration, _generateGerbers, _generatePlacements, _generateDrills, _generateIpcD356 })
                if (option.CheckAlign != ContentAlignment.MiddleRight || option.Right != _generateWithErrors.Right)
                    throw new Exception("Checkboxes must follow their labels and share a right edge.");
            _generateWithErrors.Checked = true;
            ExpectButtons(false, true, "with override before any check");
            _generateWithErrors.Checked = false;
            ExpectButtons(false, false, "after clearing override before any check");
            _cli.Text = ""; _project.Text = "";
            var missingCheck = CheckAsync();
            ExpectButtons(true, false, "immediately after pressing Check");
            await missingCheck;
            ExpectButtons(true, false, "after a failed check");
            if (!_report.Text.Contains("FAILED") || !_report.Text.Contains("Symbol Fields Table")) throw new Exception("Configuration report did not identify missing project settings under its own header.");
            results.Add("PASS configuration action reports missing settings under the Symbol Fields Table header");
            await ConfigureAsync((_, _) => throw new Exception("An unreadable project must not prompt for a destructive replacement."));
            if (!_report.Text.Contains("[FIX FAILED]")) throw new Exception("Unavailable project fixes were not reported.");
            _generateWithErrors.Checked = true;
            ExpectButtons(true, true, "with override after failed check");
            await GenerateAsync(() => true);
            if (!_report.Text.Contains("[FAILED] Production generation") || !_report.Text.Contains("Manufacturing was not cleared"))
                throw new Exception("Export must report invalid CLI input before cleanup.");
            _generateWithErrors.Checked = false;
            ExpectButtons(true, false, "after clearing override after failed check");

            var fixtureDirectory = Path.Combine(_paths.UserDataRoot, "project-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureDirectory);
            var fixture = Path.Combine(fixtureDirectory, "sample.kicad_pro");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", "configured-project.kicad_pro"), fixture);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", "configured-project.kicad_sch"), Path.ChangeExtension(fixture, ".kicad_sch"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", "placement-test.kicad_pcb"), Path.ChangeExtension(fixture, ".kicad_pcb"));
            _project.Text = fixtureDirectory;
            _revision.Text = "revB";
            ExpectButtons(false, false, "after changing the project path");
            await CheckAsync();
            ExpectButtons(true, true, "after passing all configuration checks");
            _generateWithErrors.Checked = true; _generateWithErrors.Checked = false;
            ExpectButtons(true, true, "after clearing override with a valid check");
            await RefreshAsync(manual: true);
            ExpectButtons(true, true, "after refreshing the database");

            // A new check must replace the previous success, even if the paths did not change.
            var projectJson = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture))!;
            projectJson["schematic"]!["bom_settings"]!["group_symbols"] = false;
            await File.WriteAllTextAsync(fixture, projectJson.ToJsonString());
            var recheck = CheckAsync();
            ExpectButtons(true, false, "while rechecking a previously valid project");
            await recheck;
            ExpectButtons(true, false, "after a recheck finds an error");
            var beforeDecline = await File.ReadAllBytesAsync(fixture);
            var confirmations = 0;
            var declining = ConfigureAsync((prompt, _) =>
            {
                if (prompt.Failure.Name != "Edit Tab metadata") throw new Exception("Prompt did not identify the failed check.");
                confirmations++;
                return Task.FromResult(false);
            });
            ExpectButtons(false, false, "while configuring");
            await declining;
            ExpectButtons(true, false, "after declining a fix");
            var afterDecline = await File.ReadAllBytesAsync(fixture);
            if (confirmations != 1 || !beforeDecline.SequenceEqual(afterDecline) || !_report.Text.Contains("[SKIPPED] Edit Tab metadata"))
                throw new Exception("Declining a fix changed the project or failed to report the skipped check.");
            await ConfigureAsync((prompt, _) =>
            {
                if (prompt.Failure.Name != "Edit Tab metadata") throw new Exception("Unexpected fix prompt.");
                confirmations++;
                return Task.FromResult(true);
            });
            ExpectButtons(true, true, "after an approved fix and automatic recheck");
            var backups = Directory.GetFiles(Path.Combine(fixtureDirectory, ".oeps-backups"));
            if (confirmations != 2 || backups.Length != 1 || !_report.Text.Contains("[FIXED] Edit Tab metadata") || !_report.Text.Contains("[PASSED] Edit Tab metadata"))
                throw new Exception("Approved fix, original backup, or post-fix report is missing.");
            await File.WriteAllTextAsync(Path.Combine(_paths.UserDataRoot, "configuration-fixes-report.txt"), _report.Text);
            using (var configured = new Bitmap(Width, Height))
            {
                DrawToBitmap(configured, new Rectangle(Point.Empty, Size));
                configured.Save(Path.Combine(_paths.UserDataRoot, "ui-smoke-configured.png"));
            }
            await ConfigureAsync((_, _) => throw new Exception("A passing check should not prompt for a fix."));
            results.Add("PASS separate confirmation, No preserving bytes, Yes applying the fix and backup, automatic recheck, and no prompt for passing checks");
            _revision.Text = "RevA";
            ExpectButtons(false, false, "after changing the expected revision");
            await CheckAsync();
            ExpectButtons(true, false, "after a revision mismatch");
            if (!_report.Text.Contains("[FAILED] Revision")) throw new Exception("Revision mismatch was not reported.");
            var beforeRevisionDecline = await File.ReadAllBytesAsync(fixture);
            var beforeSchematicDecline = await File.ReadAllBytesAsync(Path.ChangeExtension(fixture, ".kicad_sch"));
            var revisionPrompts = 0;
            await ConfigureAsync((prompt, _) =>
            {
                if (prompt.Failure.Name != "Revision" || _revision.Enabled) throw new Exception("Incorrect revision confirmation state.");
                revisionPrompts++;
                return Task.FromResult(false);
            });
            var afterRevisionDecline = await File.ReadAllBytesAsync(fixture);
            var afterSchematicDecline = await File.ReadAllBytesAsync(Path.ChangeExtension(fixture, ".kicad_sch"));
            if (revisionPrompts != 1 || !beforeRevisionDecline.SequenceEqual(afterRevisionDecline)
                || !beforeSchematicDecline.SequenceEqual(afterSchematicDecline) || !_report.Text.Contains("[SKIPPED] Revision"))
                throw new Exception("Declining the revision fix changed the project or failed to report Skipped.");
            await ConfigureAsync((prompt, _) =>
            {
                if (prompt.Failure.Name != "Revision") throw new Exception("Unexpected revision fix prompt.");
                revisionPrompts++;
                return Task.FromResult(true);
            });
            ExpectButtons(true, true, "after approving the revision fix");
            var revisedProject = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture))!;
            var revisedSchematic = new Oeps.KicadProductionFiles.Core.Kicad.SchematicRevisionDocument(
                await File.ReadAllTextAsync(Path.ChangeExtension(fixture, ".kicad_sch")));
            if (revisionPrompts != 2 || revisedProject["board"]!["ipc2581"]!["sch_revision"]!.GetValue<string>() != "A"
                || revisedSchematic.Revision != "A"
                || !_report.Text.Contains("[PASSED] Revision") || !_report.Text.Contains("[FIXED] Revision"))
                throw new Exception("Revision fix or final report is incorrect.");
            await File.WriteAllTextAsync(Path.Combine(_paths.UserDataRoot, "revision-report.txt"), _report.Text);
            using (var revised = new Bitmap(Width, Height))
            {
                DrawToBitmap(revised, new Rectangle(Point.Empty, Size));
                revised.Save(Path.Combine(_paths.UserDataRoot, "ui-smoke-revision.png"));
            }
            _revision.Text = "";
            await CheckAsync();
            ExpectButtons(true, false, "with a missing expected revision");
            if (!_report.Text.Contains("[FAILED] Revision")) throw new Exception("Missing expected revision was not reported.");
            _revision.Text = " reva ";
            await CheckAsync();
            ExpectButtons(true, true, "with equivalent revision spelling and spaces");
            results.Add("PASS revision input reset, mismatch, missing input, equivalent spellings, declined/approved revision fix and recheck");
            var fixtureBoard = Path.ChangeExtension(fixture, ".kicad_pcb");
            var originalBoardText = await File.ReadAllTextAsync(fixtureBoard);
            await File.WriteAllTextAsync(fixtureBoard, originalBoardText.Replace("(usegerberattributes yes)", "(usegerberattributes no)"));
            await CheckAsync();
            ExpectButtons(true, false, "after a PCB plot setting fails");
            if (!_report.Text.Contains("[FAILED] Gerber plot settings")) throw new Exception("PCB plot failure was not reported.");
            var beforePlotDecline = await File.ReadAllBytesAsync(fixtureBoard);
            var plotPrompts = 0;
            await ConfigureAsync((prompt, _) =>
            {
                if (prompt.Failure.Name != "Gerber plot settings") throw new Exception("Unexpected PCB plot confirmation.");
                plotPrompts++;
                return Task.FromResult(false);
            });
            var afterPlotDecline = await File.ReadAllBytesAsync(fixtureBoard);
            if (plotPrompts != 1 || !beforePlotDecline.SequenceEqual(afterPlotDecline) || !_report.Text.Contains("[SKIPPED] Gerber plot settings"))
                throw new Exception("Declining a PCB plot fix must preserve the board.");
            await ConfigureAsync((prompt, _) =>
            {
                if (prompt.Failure.Name != "Gerber plot settings") throw new Exception("Unexpected PCB plot fix.");
                plotPrompts++;
                return Task.FromResult(true);
            });
            ExpectButtons(true, true, "after fixing PCB plot settings");
            if (plotPrompts != 2 || await File.ReadAllTextAsync(fixtureBoard) != originalBoardText
                || !_report.Text.Contains("[PASSED] Gerber plot settings") || !_report.Text.Contains("[FIXED] Gerber plot settings")
                || Directory.GetFiles(Path.Combine(fixtureDirectory, ".oeps-backups"), "*.kicad_pcb.*.bak").Length != 1)
                throw new Exception("PCB plot repair, backup or report failed.");
            await File.WriteAllTextAsync(Path.Combine(_paths.UserDataRoot, "gerber-settings-report.txt"), _report.Text);
            using (var plotScreenshot = new Bitmap(Width, Height))
            {
                DrawToBitmap(plotScreenshot, new Rectangle(Point.Empty, Size));
                plotScreenshot.Save(Path.Combine(_paths.UserDataRoot, "ui-smoke-gerber-settings.png"));
            }
            results.Add("PASS PCB Gerber plot check, declined/approved fix, board text preservation, backup and automatic recheck");
            var preservedOnCancel = Path.Combine(fixtureDirectory, "manufacturing", "preserve-on-cancel.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(preservedOnCancel)!);
            await File.WriteAllTextAsync(preservedOnCancel, "keep");
            var reminderCalls = 0;
            await GenerateAsync(() => { reminderCalls++; return false; });
            if (reminderCalls != 1 || !_jobStatus.Text.Contains("cancelled") || await File.ReadAllTextAsync(preservedOnCancel) != "keep")
                throw new Exception("Gerber reminder cancellation failed.");
            _generateGerbers.Checked = _generatePlacements.Checked = _generateDrills.Checked = _generateIpcD356.Checked = false;
            ExpectButtons(true, false, "with no exports selected");
            _generateGerbers.Checked = _generatePlacements.Checked = _generateDrills.Checked = _generateIpcD356.Checked = true;
            ExpectButtons(true, true, "with exports selected again");
            results.Add("PASS generation selection and Gerber reminder cancellation");
            if (_args.Contains("--ui-smoke-generate"))
            {
                _cli.Text = DetectCli();
                await CheckAsync();
                var obsolete = Path.Combine(fixtureDirectory, "manufacturing", "old", "old.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(obsolete)!);
                await File.WriteAllTextAsync(obsolete, "obsolete manufacturing data");
                var generation = GenerateAsync(() => true);
                ExpectButtons(false, false, "during production generation");
                if (_project.Enabled || _revision.Enabled || _check.Enabled || _generateGerbers.Enabled || _deleteBeforeGeneration.Enabled)
                    throw new Exception("Inputs must be locked during generation.");
                await generation;
                var ipcPath = Path.Combine(fixtureDirectory, "manufacturing", "sample.d356");
                if (!_report.Text.Contains("[GENERATED] IPC-D-356 netlist") || !File.Exists(ipcPath)
                    || !(await File.ReadAllTextAsync(ipcPath)).TrimEnd().EndsWith("999", StringComparison.Ordinal))
                    throw new Exception("IPC-D-356 netlist generation failed: " + _report.Text);
                if (_report.Text.Contains("[FAILED]") || !_report.Text.Contains("[GENERATED] Gerber files") || !_report.Text.Contains("[GENERATED] Drill files"))
                    throw new Exception("Gerber/drill generation failed: " + _report.Text);
                var gerberDirectory = Path.Combine(fixtureDirectory, "manufacturing", "gerber");
                if (Directory.GetFiles(gerberDirectory, "*.gbrjob").Length != 0)
                    throw new Exception("An unrequested Gerber job file was published.");
                foreach (var type in new[] { "PTH", "NPTH" })
                    if (!File.Exists(Path.Combine(gerberDirectory, "sample-" + type + ".drl"))
                        || !File.Exists(Path.Combine(gerberDirectory, "sample-" + type + "-drl_map.gbr")))
                        throw new Exception("Missing separate drill or map output.");
                var output = Path.Combine(fixtureDirectory, "manufacturing", "assembly", "sample-pos.csv");
                if (!_report.Text.Contains("[GENERATED] Placement files") || !File.Exists(output) || File.Exists(obsolete))
                    throw new Exception("Placement generation or manufacturing cleanup failed: " + _report.Text);
                if (!_report.Text.Contains("Component count: 5")) throw new Exception("Placement component count is missing or incorrect.");
                var csv = await File.ReadAllTextAsync(output);
                foreach (var reference in new[] { "S1", "T1", "D1", "E1", "B1" })
                    if (!csv.Contains("\"" + reference + "\"")) throw new Exception("Requested placement type missing: " + reference);
                if (!csv.Contains("9.000000,-9.000000,0.000000,bottom")) throw new Exception("Incorrect placement origin or bottom X sign.");
                ExpectButtons(true, true, "after generation");
                await File.WriteAllTextAsync(Path.Combine(_paths.UserDataRoot, "production-generation-report.txt"), _report.Text);
                using var generated = new Bitmap(Width, Height);
                DrawToBitmap(generated, new Rectangle(Point.Empty, Size));
                generated.Save(Path.Combine(_paths.UserDataRoot, "ui-smoke-production.png"));
                results.Add("PASS real CLI Gerber, placement and drill exports, manufacturing cleanup, maps, component count and busy controls");
                _deleteBeforeGeneration.Checked = false;
                _generateGerbers.Checked = _generateDrills.Checked = _generateIpcD356.Checked = false;
                await File.WriteAllTextAsync(preservedOnCancel, "keep");
                var drillBefore = await File.ReadAllBytesAsync(Path.Combine(gerberDirectory, "sample-PTH.drl"));
                await GenerateAsync(() => throw new Exception("Placement-only export must not show the Gerber reminder."));
                var drillAfter = await File.ReadAllBytesAsync(Path.Combine(gerberDirectory, "sample-PTH.drl"));
                if (_report.Text.Contains("[FAILED]") || await File.ReadAllTextAsync(preservedOnCancel) != "keep"
                    || !drillBefore.SequenceEqual(drillAfter))
                    throw new Exception("Selective generation with cleanup disabled changed unrelated outputs.");
                _deleteBeforeGeneration.Checked = _generateGerbers.Checked = _generateDrills.Checked = _generateIpcD356.Checked = true;
                results.Add("PASS placement-only export skips reminder and preserves other files when cleanup is disabled");
                _deleteBeforeGeneration.Checked = _generateGerbers.Checked = _generatePlacements.Checked = _generateDrills.Checked = false;
                await GenerateAsync(() => throw new Exception("IPC-only export must not show the Gerber reminder."));
                if (!_report.Text.Contains("[GENERATED] IPC-D-356 netlist") || _report.Text.Contains("[FAILED]")
                    || _report.Text.Contains("[GENERATED] Placement files"))
                    throw new Exception("IPC-only generation did not honor the selection.");
                _deleteBeforeGeneration.Checked = _generateGerbers.Checked = _generatePlacements.Checked = _generateDrills.Checked = true;
                results.Add("PASS right-aligned checkboxes and real CLI IPC-D-356 export to manufacturing, including IPC-only selection");
            }
            _cli.Text = "changed-cli.exe";
            ExpectButtons(false, false, "after changing the CLI path");
            _generateWithErrors.Checked = true;
            _project.Text = "changed project";
            ExpectButtons(false, true, "after changing paths with override selected");
            var overrideCheck = CheckAsync();
            ExpectButtons(true, false, "during a check with override selected");
            await overrideCheck;
            ExpectButtons(true, true, "after failed check with override selected");
            _generateWithErrors.Checked = false;
            ExpectButtons(true, false, "after clearing override again");
            results.Add("PASS startup/reset, check success/failure/recheck, database refresh, and override button transitions");
            _project.Text = Path.Combine(_paths.UserDataRoot, "project with spaces"); SavePaths();
            await RefreshAsync(manual: true);
            var savedSettings = UserSettings.Load(_paths.SettingsFile);
            if (_project.Text != savedSettings.ProjectDirectory || _revision.Text.Trim() != savedSettings.Revision)
                throw new Exception("Refresh changed entries or settings were not saved.");
            results.Add("PASS generation preflight, saved paths and revision, and refresh preserving entries");
            _cli.Text = DetectCli(); _project.Text = "";
            ShowReadyReport();
            _jobStatus.Text = "Choose a project folder to get started.";
            Directory.CreateDirectory(_paths.UserDataRoot);
            using var screenshot = new Bitmap(Width, Height);
            DrawToBitmap(screenshot, new Rectangle(Point.Empty, Size));
            screenshot.Save(Path.Combine(_paths.UserDataRoot, "ui-smoke.png"));
            var projectIndex = Array.IndexOf(_args, "--ui-smoke-project");
            if (projectIndex >= 0)
            {
                if (projectIndex + 1 >= _args.Length) throw new ArgumentException("--ui-smoke-project requires a project folder.");
                _cli.Text = "";
                _project.Text = _args[projectIndex + 1];
                var revisionIndex = Array.IndexOf(_args, "--ui-smoke-revision");
                if (revisionIndex < 0 || revisionIndex + 1 >= _args.Length)
                    throw new ArgumentException("--ui-smoke-project requires --ui-smoke-revision with the expected revision.");
                _revision.Text = _args[revisionIndex + 1];
                await CheckAsync();
                if (!_report.Text.Contains("[PASSED] Symbol Fields Table") || _report.Text.Contains("All required fields exist"))
                    throw new Exception("Expected the supplied project configuration to pass: " + _report.Text);
                await File.WriteAllTextAsync(Path.Combine(_paths.UserDataRoot, "configuration-report.txt"), _report.Text);
                using var configurationScreenshot = new Bitmap(Width, Height);
                DrawToBitmap(configurationScreenshot, new Rectangle(Point.Empty, Size));
                configurationScreenshot.Save(Path.Combine(_paths.UserDataRoot, "ui-smoke-configuration.png"));
                results.Add("PASS actual project configuration under its own report header, with no CLI required");
            }
        }
        catch (Exception ex) { results.Add("FAIL " + ex); Environment.ExitCode = 1; }
        finally
        {
            Directory.CreateDirectory(_paths.UserDataRoot);
            await File.WriteAllLinesAsync(Path.Combine(_paths.UserDataRoot, "ui-smoke.txt"), results);
            Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _toolTip.Dispose(); _closing.Cancel(); }
        base.Dispose(disposing);
    }
}
