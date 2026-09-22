using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using BingWallpaperUpdater.App.Resources;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Rotation;
using BingWallpaperUpdater.Core.Scheduling;
using BingWallpaperUpdater.Windows.Autostart;

namespace BingWallpaperUpdater.App;

/// <summary>
/// The single-page Settings window (UI-01..UI-07), code-first and DPI-safe (RESEARCH Pattern 4): AutoSize
/// <see cref="TableLayoutPanel"/>s with AutoSize columns and rows, AutoSize labels, ComboBox widths measured from
/// their items at the current DPI, wrapping labels bounded by <see cref="Control.LogicalToDeviceUnits(int)"/>, and no
/// numeric Size / Width / ClientSize / Font assignment anywhere — the exact thing that clips Vietnamese at 150 %.
/// The current-image title and copyright rows are the one exception to "everything AutoSize": they are fixed boxes
/// (the wrap width by two text lines, measured from the live font at the current DPI in <see cref="FixedValueSize"/>,
/// <see cref="Label.AutoEllipsis"/> for overflow with the label's built-in full-text tooltip) so the table's preferred
/// size — and with it the form's client size — never follows the text (UI-04, Phase 04 deferred item 2: an AutoSize
/// label made every tick that landed on a longer or shorter description re-lay out and visibly resize the window).
/// The form keeps <see cref="AutoSizeMode.GrowAndShrink"/>: with a text-independent table its preferred size is
/// constant, and GrowOnly would pin a 150 % size after a move to a 100 % monitor.
/// Every control applies on change (UI-03): the handler mutates the shared <see cref="Settings"/> instance, saves
/// <c>settings.json</c> atomically and calls <see cref="RotationService.ApplySettings"/> through <see cref="Commit"/>;
/// there is no Save button. The current-image / times / last-error panel (UI-04) is a static rendering of
/// <see cref="RotationService.Snapshot"/>, refreshed only when <see cref="RotationService.StateChanged"/> fires
/// (marshalled with <see cref="Control.BeginInvoke(Action)"/>, Pitfall 6) — no timer, no countdown (locked). The
/// "Start with Windows" row (INST-02) shows the OBSERVED registry state (<see cref="RegistryAutostartManager.IsEnabled"/>:
/// Run value present and not disabled in Task Manager) rather than the persisted <see cref="Settings.Autostart"/>
/// wish; ticking it writes both HKCU values at once, unticking deletes them, and the box is re-read from the
/// registry afterwards so a failed write reverts visibly. Created on demand by
/// <see cref="TrayApplicationContext.ShowSettings"/> and disposed on close (UI-02). No dialog, balloon or toast is
/// ever shown from here (D-15): a failure is a <see cref="Log.Warn(string, Exception?)"/> line.
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal sealed class SettingsForm : Form
{
    /// <summary>Logical width at which the long metadata labels wrap (device units are recomputed per DPI).</summary>
    private const int WrapLogicalWidth = 420;

    private readonly Settings _settings;
    private readonly RotationService _rotation;
    private readonly TrayApplicationContext _context;
    private readonly CancellationToken _shutdown;
    private readonly Action _onStateChanged;
    private readonly TableLayoutPanel _table;
    private readonly List<ComboBox> _combos = [];
    private readonly List<Label> _wrapping = [];
    private readonly List<(Label Label, int Lines)> _fixed = [];
    private readonly ComboBox _interval;
    private readonly ComboBox _mode;
    private readonly ComboBox _resolution;
    private readonly ComboBox _market;
    private readonly ComboBox _monitors;
    private readonly CheckBox _autostart;
    private readonly ComboBox _language;
    private readonly Label _title;
    private readonly Label _copyright;
    private readonly Label _date;
    private readonly Label _lastChecked;
    private readonly Label _nextCheck;
    private readonly Label _lastError;
    private readonly Button _nextButton;
    private readonly Button _openCacheButton;
    private bool _loading;

    public SettingsForm(Settings settings, RotationService rotation, TrayApplicationContext context, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(rotation);
        ArgumentNullException.ThrowIfNull(context);
        _settings = settings;
        _rotation = rotation;
        _context = context;
        _shutdown = shutdown;
        _onStateChanged = OnStateChanged;
        _loading = true;

        Text = Strings.Get("Form_Title");
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;   // no resize grip fighting AutoSize
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(12);                        // logical px; scaled by AutoScale

        _table = NewTable();
        _table.Dock = DockStyle.Fill;
        Controls.Add(_table);

        // ---- settings rows -------------------------------------------------------------------------------

        // Interval (ROT-01, D-07): the tracer row. A change recomputes NextDueUtc inside ApplySettings.
        _interval = Combo(
            "IntervalCombo",
            Settings.AllowedIntervals.Select(m => new Item(m.ToString(CultureInfo.InvariantCulture), Strings.Get("Interval_" + m.ToString(CultureInfo.InvariantCulture)))).ToList(),
            settings.IntervalMinutes.ToString(CultureInfo.InvariantCulture));
        _interval.SelectedIndexChanged += (_, _) => OnValueChanged(_interval, v => Commit(s => s.IntervalMinutes = int.Parse(v, CultureInfo.InvariantCulture)));
        AddRow(_table, "Label_Interval", _interval);

        // Mode (ROT-02/03): read live by every tick (Settings.IsRandomMode); Commit alone is enough.
        _mode = Combo("ModeCombo", Settings.KnownModes.Select(m => new Item(m, ModeText(m))).ToList(), settings.Mode);
        _mode.SelectedIndexChanged += (_, _) => OnValueChanged(_mode, v => Commit(s => s.Mode = v));
        AddRow(_table, "Label_Mode", _mode);

        // Resolution (SRC-05): built from KnownResolutions so the Auto value Plan 03-02 adds appears by itself
        // (Resolution_Auto is already in the resx).
        _resolution = Combo("ResolutionCombo", Settings.KnownResolutions.Select(r => new Item(r, Strings.Get("Resolution_" + r))).ToList(), settings.Resolution);
        _resolution.SelectedIndexChanged += (_, _) => OnValueChanged(_resolution, v => Commit(s => s.Resolution = v));
        AddRow(_table, "Label_Resolution", _resolution);

        // Market (SRC-04): the mkt the API receives. Names come from ICU under the current UI culture, so no resx
        // entries are needed. Never derived from the UI language (CLAUDE.md "What NOT to Use").
        (List<Item> marketItems, string currentMarket) = MarketItems(settings);
        _market = Combo("MarketCombo", marketItems, currentMarket);
        _market.SelectedIndexChanged += (_, _) => OnValueChanged(_market, v => Commit(s => s.Market = v));
        AddRow(_table, "Label_Market", _market);

        // Monitors (WALL-03): read live at the apply step (Plan 03-04); only the mode is persisted.
        _monitors = Combo("MonitorsCombo", Settings.KnownMonitorModes.Select(m => new Item(m, MonitorText(m))).ToList(), settings.MonitorMode);
        _monitors.SelectedIndexChanged += (_, _) => OnValueChanged(_monitors, v => Commit(s => s.MonitorMode = v));
        AddRow(_table, "Label_Monitors", _monitors);

        // Start with Windows (INST-02): the box shows what Task Manager shows (observed state), so an entry the user
        // disabled there is unticked even while Settings.Autostart (the desired state) is still true.
        _autostart = new CheckBox
        {
            Name = "AutostartCheck",
            AutoSize = true,
            Text = Strings.Get("Check_Autostart"),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 0, 6),
            Checked = RegistryAutostartManager.IsEnabled(),
        };
        _autostart.CheckedChanged += (_, _) => OnAutostartChanged();
        AddRow(_table, null, _autostart);

        // Language (L10N-03): save, re-apply the UI culture, re-text the tray menu now, then close-and-reopen this
        // window from a posted message so the new strings are visible without disposing the form inside the
        // ComboBox's own event (Pitfall 5). Only the UI culture changes — Market and the regional format stay.
        _language = Combo("LanguageCombo", Settings.KnownLanguages.Select(l => new Item(l, LanguageText(l))).ToList(), settings.Language);
        _language.SelectedIndexChanged += (_, _) => OnValueChanged(_language, v =>
        {
            Commit(s => s.Language = v);
            UiCulture.Apply(v);
            _context.RetextMenu();
            BeginInvoke(() =>
            {
                Close();
                _context.ShowSettings();
            });
        });
        AddRow(_table, "Label_Language", _language);

        // ---- current image (UI-04) -------------------------------------------------------------------------

        var group = new GroupBox
        {
            Text = Strings.Get("Group_Current"),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 12, 0, 6),
            Padding = new Padding(9, 3, 9, 6),
        };
        TableLayoutPanel current = NewTable();
        current.Dock = DockStyle.Fill;
        group.Controls.Add(current);
        // Fixed two-line boxes (not AutoSize): a one-line title leaves its second line blank, a longer one wraps,
        // ends with an ellipsis and shows the full text as a tooltip — the window never resizes with the text.
        AddRow(current, "Label_Title", _title = FixedValueLabel("TitleValue", lines: 2));
        AddRow(current, "Label_Copyright", _copyright = FixedValueLabel("CopyrightValue", lines: 2));
        AddRow(current, "Label_Date", _date = ValueLabel(wrap: false));
        AddRow(_table, null, group);

        // ---- times and last error (UI-04): static until the next StateChanged, never a countdown ------------

        AddRow(_table, "Label_LastChecked", _lastChecked = ValueLabel(wrap: false));
        AddRow(_table, "Label_NextCheck", _nextCheck = ValueLabel(wrap: false));
        AddRow(_table, "Label_LastError", _lastError = ValueLabel(wrap: true));

        // ---- buttons (UI-05) -------------------------------------------------------------------------------

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 9, 0, 0),
        };
        _nextButton = new Button { Name = "NextButton", AutoSize = true, Text = Strings.Get("Button_Next"), Margin = new Padding(0, 0, 9, 0), Padding = new Padding(6, 0, 6, 0) };
        _nextButton.Click += (_, _) =>
        {
            // D-05: disabled on click, re-enabled by the tick's StateChanged. A click while a heartbeat tick already
            // holds the gate is a logged no-op (RunTickAsync returns Busy) and that tick's StateChanged re-enables it.
            _nextButton.Enabled = false;
            _ = _rotation.RunTickAsync(TickReason.Next, _shutdown);
        };
        _openCacheButton = new Button { Name = "OpenCacheButton", AutoSize = true, Text = Strings.Get("Button_OpenCache"), Margin = new Padding(0), Padding = new Padding(6, 0, 6, 0) };
        _openCacheButton.Click += (_, _) => OpenCacheFolder();
        buttons.Controls.Add(_nextButton);
        buttons.Controls.Add(_openCacheButton);
        AddRow(_table, null, buttons);

        _loading = false;
    }

    /// <summary>A ComboBox entry: the persisted value and its localized text.</summary>
    private sealed record Item(string Value, string Text)
    {
        public override string ToString() => Text;
    }

    // ---- lifecycle -------------------------------------------------------------------------------------------

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Auto-scaling (AutoScaleMode.Dpi at 96) has run by now and DeviceDpi is final for the monitor the window
        // opens on, so the device-unit bounds are re-measured here through the same path OnDpiChanged uses. The
        // constructor's values are only a first guess: if the form were ever scaled after its children were added,
        // a LogicalToDeviceUnits box set at construction would be multiplied by DeviceDpi / 96 a second time.
        RemeasureDeviceBounds();
        _rotation.StateChanged += _onStateChanged;
        RefreshStatus();
        LogLayout();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _rotation.StateChanged -= _onStateChanged;   // before the handle goes, so a late event never hits a disposed form
        base.OnFormClosed(e);
    }

    /// <summary>PerMonitorV2: the form moved to a monitor with another DPI — re-measure every ComboBox, wrap bound and fixed box at the new font/DPI.</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // WinForms has already rescaled Font and DeviceDpi inside base.OnDpiChanged, so the boxes are re-measured
        // at the new DPI; without this they would keep the old DPI's pixel size and clip to fewer lines.
        RemeasureDeviceBounds();

        foreach (ComboBox c in _combos)
        {
            if (c.IsHandleCreated)
            {
                c.Width = WidestItemWidth(c);
            }
        }

        LogLayout();
    }

    /// <summary>
    /// The one place the device-unit bounds are computed from the current font and <see cref="Control.DeviceDpi"/>:
    /// the wrap bound of every wrapping label and the fixed box of every title/copyright label. Called from
    /// <see cref="OnLoad"/> (auto-scale done, DeviceDpi final) and <see cref="OnDpiChanged"/> (font and DeviceDpi
    /// already rescaled by the base class), so both paths agree on the same numbers.
    /// </summary>
    private void RemeasureDeviceBounds()
    {
        foreach (Label l in _wrapping)
        {
            l.MaximumSize = new Size(LogicalToDeviceUnits(WrapLogicalWidth), 0);
        }

        foreach ((Label l, int lines) in _fixed)
        {
            l.Size = FixedValueSize(l, lines);
        }
    }

    /// <summary>One line per window open (and per DPI change) so the geometry is verifiable from log.txt without a screenshot.</summary>
    private void LogLayout()
    {
        Log.Info($"settings window layout dpi={DeviceDpi} client={ClientSize.Width}x{ClientSize.Height} title={_title.Width}x{_title.Height} copyright={_copyright.Width}x{_copyright.Height}");
    }

    /// <summary>Arrives on a thread-pool thread (after a tick or ApplySettings); hop to the UI thread if the window is still alive.</summary>
    private void OnStateChanged()
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke(RefreshStatus);
        }
        catch (InvalidOperationException)
        {
            // The handle was destroyed between the check and the post (the window is closing); nothing to refresh.
        }
    }

    // ---- apply-on-change -------------------------------------------------------------------------------------

    /// <summary>
    /// The apply-on-change path shared by every control (UI-03, D-09): mutate the shared instance, save it
    /// atomically (a failed save is logged, never shown — the service keeps the in-memory value, T-03-07), then let
    /// the service re-arm / re-read.
    /// </summary>
    private void Commit(Action<Settings> mutate)
    {
        mutate(_settings);
        try
        {
            _settings.Save(AppPaths.SettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("settings save failed", ex);
        }

        _rotation.ApplySettings();
    }

    /// <summary>Runs <paramref name="apply"/> with the selected item's value, except while the constructor populates the controls.</summary>
    private void OnValueChanged(ComboBox combo, Action<string> apply)
    {
        if (!_loading && combo.SelectedItem is Item it)
        {
            apply(it.Value);
        }
    }

    /// <summary>
    /// INST-02: the registry first (on -> Run value + an "enabled" StartupApproved value, which also re-enables a
    /// Task Manager "Disabled"; off -> both values deleted), then the desired state through <see cref="Commit"/>
    /// (<see cref="RotationService.ApplySettings"/> is harmless here), then the box is re-read from the registry so
    /// a failed write — already logged by the adapter — reverts it instead of lying. No dialog (T-03-15).
    /// </summary>
    private void OnAutostartChanged()
    {
        if (_loading)
        {
            return;
        }

        bool on = _autostart.Checked;
        string? exe = Environment.ProcessPath;
        if (on && exe is { Length: > 0 })
        {
            RegistryAutostartManager.Enable(exe);
        }
        else if (!on)
        {
            RegistryAutostartManager.Disable();
        }

        Commit(s => s.Autostart = on);

        bool observed = RegistryAutostartManager.IsEnabled();
        if (observed != _autostart.Checked)
        {
            _loading = true;
            try
            {
                _autostart.Checked = observed;
            }
            finally
            {
                _loading = false;
            }
        }
    }

    /// <summary>Renders <see cref="RotationService.Snapshot"/>: blanks for missing metadata (never a placeholder word), regional "g" format for times, the localized last-error text, and the Next button state (D-05).</summary>
    private void RefreshStatus()
    {
        if (IsDisposed)
        {
            return;
        }

        RotationSnapshot s = _rotation.Snapshot();
        _title.Text = s.Title ?? string.Empty;
        _copyright.Text = s.Copyright ?? string.Empty;
        _date.Text = s.Date ?? string.Empty;
        _lastChecked.Text = Fmt(s.LastCheckUtc);
        _nextCheck.Text = Fmt(s.NextDueUtc);
        _lastError.Text = s.LastError switch
        {
            LastErrorKind.Fetch => Strings.Get("Error_Fetch"),
            LastErrorKind.Apply => Strings.Get("Error_Apply"),
            LastErrorKind.ReadBack => Strings.Get("Error_ReadBack"),
            _ => string.Empty,
        };
        _nextButton.Enabled = !s.TickRunning;
        _nextButton.Text = s.TickRunning ? Strings.Get("Status_Checking") : Strings.Get("Button_Next");
    }

    /// <summary>Local time in the user's Windows regional short format — CurrentCulture is read here, never assigned.</summary>
    private static string Fmt(DateTimeOffset? v) => v?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>UI-05 / T-03-05: explorer.exe with the quoted app-owned constant path (LocalAppData may contain spaces); never user or remote text.</summary>
    private static void OpenCacheFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.CacheDir + "\"") { UseShellExecute = false });
            Log.Info("cache folder opened");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Log.Warn("open cache folder failed", ex);
        }
    }

    // ---- item texts ------------------------------------------------------------------------------------------

    private static string ModeText(string mode) => mode switch
    {
        Settings.DefaultMode => Strings.Get("Mode_Newest"),
        Settings.RandomMode => Strings.Get("Mode_Random"),
        _ => mode,
    };

    private static string MonitorText(string monitorMode) => monitorMode switch
    {
        Settings.SameMonitorMode => Strings.Get("Monitors_Same"),
        Settings.PerMonitorMode => Strings.Get("Monitors_PerMonitor"),
        _ => monitorMode,
    };

    private static string LanguageText(string language) => language switch
    {
        Settings.AutoLanguage => Strings.Get("Language_Auto"),
        "en" => Strings.Get("Language_En"),
        "vi" => Strings.Get("Language_Vi"),
        _ => language,
    };

    /// <summary>The offered markets plus the current one when it is hand-edited outside <see cref="Settings.KnownMarkets"/> (shown, never silently rewritten).</summary>
    private static (List<Item> Items, string Current) MarketItems(Settings model)
    {
        string current = model.Market;
        List<string> markets = [.. Settings.KnownMarkets];
        if (!markets.Contains(current, StringComparer.Ordinal))
        {
            markets.Add(current);
        }

        return (markets.Select(m => new Item(m, MarketText(m))).ToList(), current);
    }

    /// <summary>"English (United States) (en-US)" — the display name is localized by ICU under the current UI culture; an unknown code shows bare.</summary>
    private static string MarketText(string market)
    {
        try
        {
            return $"{CultureInfo.GetCultureInfo(market).DisplayName} ({market})";
        }
        catch (CultureNotFoundException)
        {
            return market;
        }
    }

    // ---- layout helpers (Pattern 4: every size derives from content) -----------------------------------------

    private static TableLayoutPanel NewTable()
    {
        var t = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // labels
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // editors (AutoSize, not Percent — Percent needs a fixed width)
        return t;
    }

    /// <summary>Adds an AutoSize row: a label in column 0 (or none — the editor then spans both columns) and the editor in column 1.</summary>
    private static void AddRow(TableLayoutPanel table, string? labelKey, Control editor)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (labelKey is not null)
        {
            table.Controls.Add(new Label
            {
                AutoSize = true,
                Text = Strings.Get(labelKey),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 12, 6),
            }, 0, row);
        }

        table.Controls.Add(editor, labelKey is null ? 0 : 1, row);
        if (labelKey is null)
        {
            table.SetColumnSpan(editor, 2);
        }
    }

    /// <summary>An AutoSize read-only value label; a wrapping one is bounded by the logical wrap width (height 0 lets AutoSize grow downwards).</summary>
    private Label ValueLabel(bool wrap)
    {
        var l = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 0, 6),
            UseMnemonic = false,   // metadata may contain '&'
        };
        if (wrap)
        {
            l.MaximumSize = new Size(LogicalToDeviceUnits(WrapLogicalWidth), 0);
            _wrapping.Add(l);
        }

        return l;
    }

    /// <summary>
    /// A read-only value label with a fixed box (not AutoSize): the wrap width by <paramref name="lines"/> text lines,
    /// measured by <see cref="FixedValueSize"/> from the live font at the current DPI here as a first value and
    /// re-measured by <see cref="RemeasureDeviceBounds"/> in <see cref="OnLoad"/> and <see cref="OnDpiChanged"/>.
    /// <see cref="Label.AutoEllipsis"/> word-wraps inside the box, ends a longer text with
    /// an ellipsis and shows the full text in the label's own internal ToolTip on hover (created and disposed with the
    /// label, so no extra component and nothing new for the per-open GDI count). The box contributes its
    /// <see cref="Control.Size"/> — not the text's preferred size — to the <see cref="TableLayoutPanel"/> measurement,
    /// which is what keeps the client size independent of the text.
    /// </summary>
    private Label FixedValueLabel(string name, int lines)
    {
        var l = new Label
        {
            Name = name,   // UI Automation id
            AutoSize = false,
            AutoEllipsis = true,
            UseMnemonic = false,   // metadata may contain '&'
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 6, 0, 6),
        };
        l.Size = FixedValueSize(l, lines);
        _fixed.Add((l, lines));
        return l;
    }

    /// <summary>
    /// The device-unit box for exactly <paramref name="lines"/> fully visible text lines at the label's current font
    /// and DPI: width = the logical wrap width in device units, height = the measured height of a
    /// <paramref name="lines"/>-line sample ("Wg" per line) with the same flags the <see cref="Label"/> paints with
    /// (<see cref="TextFormatFlags.WordBreak"/> | <see cref="TextFormatFlags.TextBoxControl"/>, plus EndEllipsis for
    /// AutoEllipsis). TextBoxControl hides a partially visible last line, so measuring the sample with it yields
    /// exactly N whole lines. No numeric literal enters a <see cref="Size"/> and <see cref="Control.Font"/> is never
    /// assigned (Pattern 4); the height is never derived from Font.Height arithmetic.
    /// </summary>
    private Size FixedValueSize(Label l, int lines)
    {
        int width = LogicalToDeviceUnits(WrapLogicalWidth);
        string sample = string.Join("\n", Enumerable.Repeat("Wg", lines));
        int height = TextRenderer.MeasureText(sample, l.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
        return new Size(width, height);
    }

    /// <summary>
    /// A DropDownList ComboBox named for automation, populated with <paramref name="items"/> and the matching value
    /// selected (the first item when none matches). Its width is measured from the widest item once the handle
    /// exists (the font and DPI are known only then) and again after a DPI change, so no pixel number is ever written.
    /// </summary>
    private ComboBox Combo(string name, IReadOnlyList<Item> items, string selectedValue)
    {
        var c = new ComboBox
        {
            Name = name,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 0, 3),
        };
        foreach (Item it in items)
        {
            c.Items.Add(it);
        }

        int index = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Value, selectedValue, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (items.Count > 0)
        {
            c.SelectedIndex = index;
        }

        c.HandleCreated += (_, _) => c.Width = WidestItemWidth(c);
        _combos.Add(c);
        return c;
    }

    /// <summary>The widest item at the control's current font/DPI plus the drop-down arrow, never below what WinForms wants for the current text.</summary>
    private static int WidestItemWidth(ComboBox c)
    {
        int widest = c.PreferredSize.Width;
        int arrow = SystemInformation.VerticalScrollBarWidth + c.LogicalToDeviceUnits(8);
        foreach (object item in c.Items)
        {
            int w = TextRenderer.MeasureText(item.ToString(), c.Font).Width + arrow;
            if (w > widest)
            {
                widest = w;
            }
        }

        return widest;
    }
}
