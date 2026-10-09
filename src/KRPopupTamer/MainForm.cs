using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace KRPopupTamer;

// The window (hidden to the tray when closed) and the 1-second loop that attaches to the game, keeps the
// selected options applied, and collects statistics.
sealed class MainForm : Form
{
    readonly Settings _settings = Settings.Load();
    readonly NotifyIcon _tray = new();
    readonly Label _status = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    readonly Label _info = new() { AutoSize = true };
    readonly ListView _options = new() { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false };
    readonly Label _description = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly ListBox _log = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    readonly Button _update = new() { AutoSize = true, Visible = false, Anchor = AnchorStyles.Right | AnchorStyles.Top };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly Dictionary<string, long> _session = [], _lastCount = [];
    readonly Dictionary<string, string> _errors = [];
    GameProcess? _game;
    Patcher? _patcher;
    Updater.Release? _release;
    DateTime _nextUpdateCheck;
    bool _supported, _multiplayer, _allowShow, _exiting, _balloonShown, _checkingUpdate;
    string? _lastError;

    public MainForm(bool startMinimized)
    {
        _allowShow = !(startMinimized || _settings.StartMinimized);
        Text = $"KR Popup Tamer {Updater.Current} - Kingdoms Reborn";
        Icon = AppIcon();
        ClientSize = new Size(Scale(900), Scale(720));
        MinimumSize = new Size(Scale(700), Scale(580));
        StartPosition = FormStartPosition.CenterScreen;

        var launch = new Button { Text = "Launch Kingdoms Reborn", AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        launch.Click += (_, _) => LaunchGame();
        _update.Click += (_, _) => InstallUpdate();
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var labels = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        labels.Controls.AddRange([_status, _info]);
        header.Controls.Add(labels, 0, 0);
        header.Controls.Add(_update, 1, 0);
        header.Controls.Add(launch, 2, 0);

        _options.Columns.Add("Suppress / enable", Scale(400));
        _options.Columns.Add("Type", Scale(80));
        _options.Columns.Add("State", Scale(120));
        _options.Columns.Add("This session", Scale(95), HorizontalAlignment.Right);
        _options.Columns.Add("Total", Scale(80), HorizontalAlignment.Right);
        foreach (var option in Catalog.Options)
        {
            var item = new ListViewItem(option.Name) { Tag = option, Checked = _settings.IsEnabled(option) };
            item.SubItems.AddRange([option.Kind == OptionKind.Popup ? "Popup" : "Gameplay", "Waiting for game", "", ""]);
            _options.Items.Add(item);
        }
        // The ListView also raises ItemChecked (off, then on) when its window is created; act only on real changes,
        // after the event burst is over.
        _options.ItemChecked += (_, e) => { var item = e.Item; BeginInvoke(() => OnOptionToggled(item)); };
        _options.SelectedIndexChanged += (_, _) =>
            _description.Text = _options.SelectedItems.Count > 0 ? ((GameOption)_options.SelectedItems[0].Tag!).Description : "";
        _description.Text = "Select an option to see what it does.";

        var factors = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        foreach (var option in Catalog.Options.Where(o => o.FactorLabel != null))
        {
            var box = new NumericUpDown { Minimum = 1.0m, Maximum = 10.0m, Increment = 0.1m, DecimalPlaces = 1, Width = Scale(60), Value = _settings.FactorOf(option) };
            box.ValueChanged += (_, _) => OnFactorChanged(option, box.Value);
            factors.Controls.Add(new Label { Text = option.FactorLabel, AutoSize = true, Padding = new Padding(0, Scale(5), 0, 0) });
            factors.Controls.Add(box);
        }

        var startWithWindows = new CheckBox { Text = "Start with Windows", AutoSize = true, Checked = Settings.StartWithWindows };
        startWithWindows.CheckedChanged += (_, _) =>
        {
            if (Settings.SetStartWithWindows(startWithWindows.Checked) is { } error) Log(error);
        };
        var startMinimizedBox = new CheckBox { Text = "Start minimized to the tray", AutoSize = true, Checked = _settings.StartMinimized };
        startMinimizedBox.CheckedChanged += (_, _) => { _settings.StartMinimized = startMinimizedBox.Checked; SaveSettings(); };
        var exit = new Button { Text = "Exit", AutoSize = true };
        exit.Click += (_, _) => ExitApp();
        var checkUpdates = new Button { Text = "Check for updates", AutoSize = true };
        checkUpdates.Click += async (_, _) => await CheckForUpdates(manual: true);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        footer.Controls.AddRange([startWithWindows, startMinimizedBox, checkUpdates, exit]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, (Font.Height + Scale(9)) * (Catalog.Options.Length + 2))); // all options visible
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height * 3 + Scale(8)));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(header);
        layout.Controls.Add(_options);
        layout.Controls.Add(factors);
        layout.Controls.Add(_description);
        layout.Controls.Add(new Label { Text = "Activity (changes stay in the game until it closes; uncheck an option to undo it)", AutoSize = true });
        layout.Controls.Add(_log);
        layout.Controls.Add(footer);
        Controls.Add(layout);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Launch Kingdoms Reborn", null, (_, _) => LaunchGame());
        menu.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdates(manual: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.Icon = Icon;
        _tray.Text = "KR Popup Tamer";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowWindow();

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Log("Started. Waiting for Kingdoms Reborn; selected options are applied as soon as it runs. Single-player only.");
        Tick();
    }

    void Tick()
    {
        if (DateTime.Now >= _nextUpdateCheck) // at start, then daily
        {
            _nextUpdateCheck = DateTime.Now.AddDays(1);
            _ = CheckForUpdates(manual: false);
        }
        try
        {
            if (_game == null)
            {
                var game = GameProcess.TryOpen();
                if (game == null)
                {
                    SetStatus("Game not running", "Start Kingdoms Reborn (Steam or the button). Options apply automatically.");
                    return;
                }
                Attach(game);
            }
            if (_game!.HasExited)
            {
                Log("Game exited. Waiting for it to start again.");
                Detach();
                return;
            }
            if (!_supported || _patcher == null) return;
            var info = GameInfo.Read(_game);
            if (info != null) _patcher.SetLocalPlayer(info.PlayerId);
            var multiplayer = info is { IsSinglePlayer: false };
            if (multiplayer != _multiplayer)
            {
                _multiplayer = multiplayer;
                Log(multiplayer ? "Multiplayer game detected: all options are switched off (they would desync the game)."
                                : "Single-player game: options are active again.");
            }
            Reconcile();
            CollectCounts();
            SetStatus($"Attached to Kingdoms Reborn (PID {_game.Pid})", info == null
                ? "In menu or loading."
                : _multiplayer ? "Multiplayer game: options are disabled."
                : $"Year {info.Year}  |  card choice open: {info.HandDescription}  |  queued card choices: {info.QueuedHands} ({info.QueuedYearlyActions} Yearly Action)");
            _lastError = null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            if (_game == null) SetStatus("Cannot access the game", "If the game runs as administrator, run this app as administrator too.");
            if (e.Message != _lastError) Log($"Error: {e.Message}");
            _lastError = e.Message;
            if (_game?.HasExited ?? false) Detach();
        }
    }

    void Attach(GameProcess game)
    {
        try
        {
            var (stamp, size) = game.ImageIdentity();
            _game = game;
            _supported = stamp == Catalog.BuildTimeDateStamp && size == Catalog.BuildSizeOfImage;
            if (!_supported)
            {
                SetStatus("Unsupported game build", $"PE stamp 0x{stamp:x8}. This version supports 0x{Catalog.BuildTimeDateStamp:x8}; nothing will be changed.");
                Log($"Attached to PID {game.Pid}, but this game build is not supported. Nothing will be changed.");
                foreach (ListViewItem item in _options.Items) item.SubItems[2].Text = "Unsupported build";
                return;
            }
            var patcher = new Patcher(game, Catalog.Options);
            foreach (var option in Catalog.Options) _lastCount[option.Id] = patcher.CountOf(option); // count only new events
            _patcher = patcher;
            Log($"Attached to Kingdoms Reborn (PID {game.Pid}).");
        }
        catch
        {
            _game = game; // so Detach disposes it; the next tick attaches again
            Detach();
            throw;
        }
    }

    void Detach()
    {
        _game?.Dispose();
        _game = null;
        _patcher = null;
        _supported = _multiplayer = false;
        _errors.Clear();
        _lastCount.Clear();
        foreach (ListViewItem item in _options.Items) item.SubItems[2].Text = "Waiting for game";
    }

    // Brings the game in line with the checkboxes: applies checked options, restores unchecked ones.
    // In a multiplayer game every option is restored.
    void Reconcile()
    {
        foreach (ListViewItem item in _options.Items)
        {
            var option = (GameOption)item.Tag!;
            if (_errors.ContainsKey(option.Id)) { item.SubItems[2].Text = "Error"; continue; }
            var want = _settings.IsEnabled(option) && !_multiplayer;
            try
            {
                if (option.FactorLabel != null) _patcher!.SetFactor(option, (int)(_settings.FactorOf(option) * 100));
                var before = _patcher!.StateOf(option);
                if (!_patcher.Set(option, want))
                {
                    item.SubItems[2].Text = "Switching..."; // a game thread was inside the code; retry next second
                    continue;
                }
                var after = _patcher.StateOf(option);
                if (after != before) Log($"{option.Name}: {(want ? "ON" + FactorText(option) : "OFF (game restored)")}");
                item.SubItems[2].Text = after switch { PatchState.On => "Active" + FactorText(option), PatchState.Off => "Off", _ => after.ToString() };
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                if (_game!.HasExited) throw;
                _errors[option.Id] = e.Message;
                item.SubItems[2].Text = "Error";
                Log($"{option.Name}: {e.Message} (toggle the option to retry)");
            }
        }
    }

    void CollectCounts()
    {
        var changed = false;
        foreach (ListViewItem item in _options.Items)
        {
            var option = (GameOption)item.Tag!;
            if (!option.Patches.OfType<SkipPatch>().Any()) { item.SubItems[3].Text = item.SubItems[4].Text = "-"; continue; }
            var count = _patcher!.CountOf(option);
            var last = _lastCount.GetValueOrDefault(option.Id);
            if (count < last) last = 0; // a new counting page
            var delta = count - last;
            _lastCount[option.Id] = count;
            if (delta > 0)
            {
                _session[option.Id] = _session.GetValueOrDefault(option.Id) + delta;
                _settings.TotalSuppressed[option.Id] = _settings.TotalSuppressed.GetValueOrDefault(option.Id) + delta;
                Log($"Suppressed {delta} x {option.Name}");
                changed = true;
            }
            item.SubItems[3].Text = _session.GetValueOrDefault(option.Id).ToString("N0");
            item.SubItems[4].Text = _settings.TotalSuppressed.GetValueOrDefault(option.Id).ToString("N0");
        }
        if (!changed) return;
        SaveSettings();
        _tray.Text = $"KR Popup Tamer - {_session.Values.Sum():N0} suppressed this session";
    }

    void OnOptionToggled(ListViewItem item)
    {
        var option = (GameOption)item.Tag!;
        if (item.Checked == _settings.IsEnabled(option)) return; // no real change (e.g. window creation)
        _settings.Enabled[option.Id] = item.Checked;
        SaveSettings();
        _errors.Remove(option.Id);
        if (_patcher == null) return;
        try
        {
            Reconcile();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log($"Error: {e.Message}");
            if (_game?.HasExited ?? false) Detach();
        }
    }

    string FactorText(GameOption option) => option.FactorLabel != null ? $" x{_settings.FactorOf(option):0.0}" : "";

    void OnFactorChanged(GameOption option, decimal factor)
    {
        _settings.Factors[option.Id] = factor;
        SaveSettings();
        if (_patcher == null) return;
        try
        {
            Reconcile(); // writes the new factor into the game
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log($"Error: {e.Message}");
            if (_game?.HasExited ?? false) Detach();
        }
    }

    async Task CheckForUpdates(bool manual)
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        try
        {
            var release = await Updater.CheckAsync();
            if (release == null)
            {
                if (manual) Log($"KR Popup Tamer {Updater.Current} is the latest version.");
                return;
            }
            if (!manual && release.Version == _release?.Version) return;
            _release = release;
            _update.Text = $"Update to {release.Version}";
            _update.Visible = true;
            Log($"KR Popup Tamer {release.Version} is available (this is {Updater.Current}). Click \"Update to {release.Version}\" to install it. What's new: {release.Page}");
            if (!Visible) _tray.ShowBalloonTip(5000, "KR Popup Tamer", $"Version {release.Version} is available. Open the window to update.", ToolTipIcon.Info);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            if (manual) Log($"Could not check for updates: {e.Message}");
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    async void InstallUpdate()
    {
        if (_release is not { } release) return;
        var answer = MessageBox.Show(this, $"Install KR Popup Tamer {release.Version} and restart the app?\n\n" +
            "Your options and statistics are kept. Options stay active in the running game during the restart.",
            "Update KR Popup Tamer", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;
        _update.Enabled = false;
        Log($"Downloading KR Popup Tamer {release.Version}...");
        try
        {
            await Updater.InstallAsync(release);
            Log("Installed. Restarting...");
            ExitApp();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                   or InvalidDataException or Win32Exception)
        {
            Log($"Update failed: {e.Message} You can download it from {release.Page}");
            _update.Enabled = true;
        }
    }

    void SaveSettings()
    {
        if (_settings.Save() is { } error && error != _lastError) { Log(error); _lastError = error; }
    }

    void SetStatus(string status, string info)
    {
        _status.Text = status;
        _info.Text = info;
    }

    void Log(string message)
    {
        _log.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (_log.Items.Count > 1000) _log.Items.RemoveAt(0);
        _log.TopIndex = _log.Items.Count - 1;
    }

    void LaunchGame()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"steam://rungameid/{Catalog.SteamAppId}") { UseShellExecute = true });
            Log("Asked Steam to start Kingdoms Reborn.");
        }
        catch (Win32Exception e)
        {
            Log($"Could not start the game through Steam: {e.Message}");
        }
    }

    void ShowWindow()
    {
        _allowShow = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        _timer.Stop();
        SaveSettings();
        _tray.Visible = false;
        _game?.Dispose();
        Application.Exit();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!_allowShow)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_balloonShown)
            {
                _tray.ShowBalloonTip(3000, "KR Popup Tamer", "Still running in the tray. Right-click the icon to exit.", ToolTipIcon.Info);
                _balloonShown = true;
            }
            return;
        }
        SaveSettings();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    // Pixel sizes are given for 96 DPI; fonts already follow the display scale, so scale sizes the same way.
    int Scale(int pixels) => (int)Math.Round(pixels * DeviceDpi / 96.0);

    static Icon AppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var background = new SolidBrush(Color.FromArgb(46, 94, 44));
            g.FillEllipse(background, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "KR", font, new Rectangle(0, 0, 32, 32), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
