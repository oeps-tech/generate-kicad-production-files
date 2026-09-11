using System.Diagnostics;
using System.Text.RegularExpressions;
using Oeps.KicadProductionFiles.Core.Configuration;
using Oeps.KicadProductionFiles.Core.Updates;

namespace Oeps.KicadProductionFiles.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (AppInstanceCoordinator.IsAppRunning())
        {
            AppInstanceCoordinator.ActivateExisting();
            return;
        }
        using var launcherLock = new Mutex(false, AppInstanceCoordinator.LauncherMutexName, out var created);
        if (!created)
        {
            MessageBox.Show("OEPS KiCad Production Files is already starting. Please wait for its window.",
                "OEPS KiCad Production Files", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.Run(new LauncherForm(args));
    }
}

internal sealed class LauncherForm : Form
{
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, Text = "Checking for software updates…" };
    private readonly Button _retry = new() { Text = "Retry", AutoSize = true, Enabled = false };
    private readonly CancellationTokenSource _closing = new();
    private readonly string[] _args;
    private readonly string _userRoot = new AppPaths().UserDataRoot;
    private bool _busy;
    private bool _recoverWithoutUpdate;

    public LauncherForm(string[] args)
    {
        SuspendLayout();
        _args = args;
        Text = "OEPS KiCad Production Files — starting";
        using (var iconStream = typeof(LauncherForm).Assembly.GetManifestResourceStream("Oeps.AppIcon"))
        {
            if (iconStream is not null)
            {
                using var appIcon = new Icon(iconStream);
                Icon = (Icon)appIcon.Clone();
            }
        }
        ClientSize = new Size(480, 185);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(460, 220);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 10);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), RowCount = 2, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(_status, 0, 0);
        panel.Controls.Add(_retry, 0, 1);
        Controls.Add(panel);
        _retry.Click += async (_, _) => await StartAsync();
        Shown += async (_, _) => await StartAsync();
        FormClosing += (_, _) => _closing.Cancel();
        ResumeLayout(true);
    }

    private async Task StartAsync()
    {
        if (_busy) return;
        _busy = true;
        _retry.Enabled = false;
        try
        {
            if (AppInstanceCoordinator.IsAppRunning())
            {
                AppInstanceCoordinator.ActivateExisting();
                Close();
                return;
            }
            var store = new UpdateStore(_userRoot);
            var state = store.LoadState();
            var configuration = AppConfiguration.Load(AppContext.BaseDirectory, _userRoot);
            InstalledVersion? candidate = null;
            string? updateError = null;
            try
            {
                var packageIndex = _recoverWithoutUpdate ? -1 : Array.IndexOf(_args, "--install-package");
                if (packageIndex >= 0 && packageIndex + 1 < _args.Length)
                {
                    var package = Path.GetFullPath(_args[packageIndex + 1]);
                    var match = Regex.Match(Path.GetFileName(package), "^" + Regex.Escape(configuration.PackagePrefix) + @"-(.+)-win-x64\.zip$");
                    if (!match.Success || !SemanticVersion.TryParse(match.Groups[1].Value, out var bundledVersion))
                        throw new InvalidDataException("The bundled application package has an invalid versioned filename.");
                    if (state.Current is null || bundledVersion!.CompareTo(SemanticVersion.Parse(state.Current.Version)) > 0)
                    {
                        _status.Text = "Validating the bundled application…";
                        var checksum = UpdateStore.ParseChecksum(await File.ReadAllTextAsync(package + ".sha256", _closing.Token), Path.GetFileName(package));
                        candidate = await store.StagePackageAsync(package, checksum, bundledVersion!, cancellationToken: _closing.Token);
                    }
                }
                _status.Text = "Checking the latest published GitHub release…";
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
                var releases = new GitHubReleaseClient(http, configuration.GitHubOwner, configuration.GitHubRepository, configuration.PackagePrefix);
                var release = _recoverWithoutUpdate ? null : await releases.GetLatestReleaseAsync(_closing.Token);
                var installedVersion = candidate?.Version ?? state.Current?.Version ?? state.Previous?.Version;
                if (release is not null && (installedVersion is null || release.Version.CompareTo(SemanticVersion.Parse(installedVersion)) > 0)
                    && (installedVersion is null || ConfirmUpdate(installedVersion, release.VersionText)))
                {
                    _status.Text = $"Downloading and validating version {release.VersionText}…";
                    candidate = await store.DownloadAndStageAsync(http, release, _closing.Token);
                }
            }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                updateError = ex.Message;
                Log("Update check/install: " + ex.Message);
                if (ex is IncompatibleUpdateException)
                    MessageBox.Show(this, ex.Message, "Runtime upgrade needed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            var candidates = new[] { candidate, state.Current, state.Previous }
                .Where(v => v is not null).Cast<InstalledVersion>().DistinctBy(v => v.DirectoryName).ToArray();
            if (candidates.Length == 0)
                throw new InvalidOperationException("No working app is installed yet. Connect to the internet and select Retry, or reinstall from a full release installer. " + updateError);

            var startupErrors = new List<string>();
            foreach (var installed in candidates)
            {
                _closing.Token.ThrowIfCancellationRequested();
                if (AppInstanceCoordinator.IsAppRunning())
                {
                    AppInstanceCoordinator.ActivateExisting();
                    Close();
                    return;
                }
                _status.Text = $"Starting version {installed.Version}…";
                try
                {
                    await LaunchAndWaitAsync(store.GetExecutablePath(installed), _closing.Token);
                    store.MarkWorking(installed);
                    Close();
                    return;
                }
                catch (StartupStillRunningException)
                {
                    _recoverWithoutUpdate = state.Current is not null || state.Previous is not null;
                    throw;
                }
                catch (OperationCanceledException) when (_closing.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    startupErrors.Add($"{installed.Version}: {ex.Message}");
                    Log("Application startup: " + startupErrors[^1]);
                }
            }
            throw new InvalidOperationException("The application could not start, including the saved recovery version. " + string.Join(" ", startupErrors));
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log(ex.Message);
            _status.Text = ex.Message;
            _retry.Enabled = true;
        }
        finally { _busy = false; }
    }

    private bool ConfirmUpdate(string currentVersion, string newVersion)
    {
        using var dialog = new Form
        {
            Text = "OEPS KiCad Production Files — update available",
            ClientSize = new Size(440, 190), Font = Font,
            AutoScaleDimensions = new SizeF(96f, 96f),
            AutoScaleMode = AutoScaleMode.Dpi,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            Icon = Icon,
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false
        };
        var message = new Label
        {
            Dock = DockStyle.Fill, Padding = new Padding(20),
            Text = $"A new version is available. Do you want to update?\n\nCurrent version: {currentVersion}\nNew version: {newVersion}"
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8),
            FlowDirection = FlowDirection.RightToLeft
        };
        var later = new Button { Text = "Not now", AutoSize = true, DialogResult = DialogResult.Cancel };
        var update = new Button { Text = "Update now", AutoSize = true, DialogResult = DialogResult.OK };
        buttons.Controls.Add(later);
        buttons.Controls.Add(update);
        dialog.Controls.Add(message);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = update;
        dialog.CancelButton = later;
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private static async Task LaunchAndWaitAsync(string executable, CancellationToken cancellationToken)
    {
        var eventName = @"Local\OEPS.KicadProductionFiles.Ready." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false };
        var bundledRuntime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "runtime"));
        if (File.Exists(Path.Combine(bundledRuntime, "dotnet.exe")))
        {
            start.Environment["DOTNET_ROOT_X64"] = bundledRuntime;
            start.Environment["DOTNET_ROOT"] = bundledRuntime;
        }
        start.ArgumentList.Add("--startup-ready");
        start.ArgumentList.Add(eventName);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the app.");
        var deadline = DateTime.UtcNow.AddSeconds(35);
        while (DateTime.UtcNow < deadline)
        {
            if (ready.WaitOne(0)) return;
            if (process.HasExited) throw new InvalidOperationException($"The app exited before it was ready (exit code {process.ExitCode}).");
            await Task.Delay(100, cancellationToken);
        }
        if (process.HasExited) throw new InvalidOperationException($"The app exited before it was ready (exit code {process.ExitCode}).");
        // Preserve a running operator session or file export. Recovery can run once this child exits.
        throw new StartupStillRunningException("The new app has not reported that it is ready. Its window may show an error. Close that app, then select Retry. The last working version is preserved.");
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(_userRoot);
            var path = Path.Combine(_userRoot, "launcher.log");
            if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024) File.WriteAllText(path, "");
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class StartupStillRunningException(string message) : Exception(message);
}
