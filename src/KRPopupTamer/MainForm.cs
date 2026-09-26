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
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly Dictionary<string, long> _session = [], _lastCount = [];
    readonly Dictionary<string, string> _errors = [];
    GameProcess? _game;
    Patcher? _patcher;
    bool _supported, _multiplayer, _allowShow, _exiting, _balloonShown;
    string? _lastError;

    public MainForm(bool startMinimized)
    {
        _allowShow = !(startMinimized || _settings.StartMinimized);
        Text = "KR Popup Tamer - Kingdoms Reborn";
        Icon = AppIcon();
        ClientSize = new Size(820, 600);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;

        var launch = new Button { Text = "Launch Kingdoms Reborn", AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        launch.Click += (_, _) => LaunchGame();
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var labels = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        labels.Controls.AddRange([_status, _info]);
        header.Controls.Add(labels, 0, 0);
        header.Controls.Add(launch, 1, 0);

        _options.Columns.Add("Suppress / enable", 330);
        _options.Columns.Add("Type", 80);
        _options.Columns.Add("State", 140);
        _options.Columns.Add("This session", 90, HorizontalAlignment.Right);
        _options.Columns.Add("Total", 80, HorizontalAlignment.Right);
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

        var startWithWindows = new CheckBox { Text = "Start with Windows", AutoSize = true, Checked = Settings.StartWithWindows };
        startWithWindows.CheckedChanged += (_, _) =>
        {
            if (Settings.SetStartWithWindows(startWithWindows.Checked) is { } error) Log(error);
        };
        var startMinimizedBox = new CheckBox { Text = "Start minimized to the tray", AutoSize = true, Checked = _settings.StartMinimized };
        startMinimizedBox.CheckedChanged += (_, _) => { _settings.StartMinimized = startMinimizedBox.Checked; SaveSettings(); };
        var exit = new Button { Text = "Exit", AutoSize = true };
        exit.Click += (_, _) => ExitApp();
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        footer.Controls.AddRange([startWithWindows, startMinimizedBox, exit]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(header);
        layout.Controls.Add(_options);
        layout.Controls.Add(_description);
        layout.Controls.Add(new Label { Text = "Activity (changes stay in the game until it closes; uncheck an option to undo it)", AutoSize = true });
        layout.Controls.Add(_log);
        layout.Controls.Add(footer);
        Controls.Add(layout);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Launch Kingdoms Reborn", null, (_, _) => LaunchGame());
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
                var before = _patcher!.StateOf(option);
                if (!_patcher.Set(option, want))
                {
                    item.SubItems[2].Text = "Switching..."; // a game thread was inside the code; retry next second
                    continue;
                }
                var after = _patcher.StateOf(option);
                if (after != before) Log($"{option.Name}: {(want ? "ON" : "OFF (game restored)")}");
                item.SubItems[2].Text = after switch { PatchState.On => "Active", PatchState.Off => "Off", _ => after.ToString() };
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
