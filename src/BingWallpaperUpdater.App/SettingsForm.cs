using System.Globalization;
using System.Runtime.Versioning;
using BingWallpaperUpdater.App.Resources;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Rotation;

namespace BingWallpaperUpdater.App;

/// <summary>
/// The single-page Settings window (UI-01..UI-07), code-first and DPI-safe (RESEARCH Pattern 4): one AutoSize
/// <see cref="TableLayoutPanel"/> with AutoSize columns and rows, AutoSize labels, ComboBox widths measured from
/// their items at the current DPI, and no numeric Size / Width / ClientSize / Font assignment anywhere — the exact
/// thing that clips Vietnamese at 150 %. Every control applies on change (UI-03): the handler mutates the shared
/// <see cref="Settings"/> instance, saves <c>settings.json</c> atomically and calls
/// <see cref="RotationService.ApplySettings"/> through <see cref="Commit"/>; there is no Save button. Created on
/// demand by <see cref="TrayApplicationContext.ShowSettings"/> and disposed on close (UI-02). No dialog, balloon or
/// toast is ever shown from here (D-15): a failure is a <see cref="Log.Warn(string, Exception?)"/> line.
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal sealed class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly RotationService _rotation;
    private readonly TrayApplicationContext _context;
    private readonly CancellationToken _shutdown;
    private readonly TableLayoutPanel _table;
    private readonly List<ComboBox> _combos = [];
    private readonly ComboBox _interval;
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

        _table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
        };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // labels
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // editors (AutoSize, not Percent — Percent needs a fixed width)
        Controls.Add(_table);

        // Interval (ROT-01, D-07): the tracer row. A change recomputes NextDueUtc inside ApplySettings.
        _interval = Combo(
            "IntervalCombo",
            Settings.AllowedIntervals.Select(m => new Item(m.ToString(CultureInfo.InvariantCulture), Strings.Get("Interval_" + m.ToString(CultureInfo.InvariantCulture)))).ToList(),
            settings.IntervalMinutes.ToString(CultureInfo.InvariantCulture));
        _interval.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && _interval.SelectedItem is Item it)
            {
                Commit(s => s.IntervalMinutes = int.Parse(it.Value, CultureInfo.InvariantCulture));
            }
        };
        AddRow("Label_Interval", _interval);

        _loading = false;
    }

    /// <summary>A ComboBox entry: the persisted value and its localized text.</summary>
    private sealed record Item(string Value, string Text)
    {
        public override string ToString() => Text;
    }

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

    /// <summary>Adds an AutoSize row: a label in column 0 (or none — the editor then spans both columns) and the editor in column 1.</summary>
    private void AddRow(string? labelKey, Control editor)
    {
        int row = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (labelKey is not null)
        {
            _table.Controls.Add(new Label
            {
                AutoSize = true,
                Text = Strings.Get(labelKey),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 12, 6),
            }, 0, row);
        }

        _table.Controls.Add(editor, labelKey is null ? 0 : 1, row);
        if (labelKey is null)
        {
            _table.SetColumnSpan(editor, 2);
        }
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

    /// <summary>PerMonitorV2: the form moved to a monitor with another DPI — re-measure every ComboBox at the new font/DPI.</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        foreach (ComboBox c in _combos)
        {
            if (c.IsHandleCreated)
            {
                c.Width = WidestItemWidth(c);
            }
        }
    }
}
