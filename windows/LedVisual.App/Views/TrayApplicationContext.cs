using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using LedVisual.App.Controllers;

namespace LedVisual.App.Views;

/// <summary>
/// Headless system-tray ApplicationContext: no main window, no taskbar icon,
/// and no on-screen popups/toasts/banners when triggered.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppCoordinator _coordinator;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly ContextMenuStrip _contextMenu;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _setupWarningItem;
    private readonly ToolStripMenuItem _lastActivityItem;
    private readonly ToolStripMenuItem _lastErrorItem;
    private readonly ToolStripMenuItem _captureNowItem;

    private SettingsForm? _settingsForm;

    public TrayApplicationContext()
    {
        _coordinator = new AppCoordinator();
        _trayIcon = CreateDiscreetTrayIcon();

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _setupWarningItem = new ToolStripMenuItem("⚠ API key missing — Configure…");
        _setupWarningItem.Click += (_, _) => OpenSettings();

        _lastActivityItem = new ToolStripMenuItem { Enabled = false };
        _lastErrorItem = new ToolStripMenuItem { Enabled = false, Visible = false };

        _captureNowItem = new ToolStripMenuItem("Capture Screen Now");
        _captureNowItem.Click += (_, _) => _coordinator.TriggerManualScreenshot();

        var test3Item = new ToolStripMenuItem("Test 3 Blinks");
        test3Item.Click += (_, _) => _coordinator.TestBlink([3]);

        var testMultiItem = new ToolStripMenuItem("Test 1, 3");
        testMultiItem.Click += (_, _) => _coordinator.TestBlink([1, 3]);

        var testFailItem = new ToolStripMenuItem("Test Fail");
        testFailItem.Click += (_, _) => _coordinator.TestErrorBlink();

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => QuitApplication();

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            _setupWarningItem,
            _lastActivityItem,
            _lastErrorItem,
            new ToolStripSeparator(),
            _captureNowItem,
            test3Item,
            testMultiItem,
            testFailItem,
            new ToolStripSeparator(),
            settingsItem,
            quitItem
        });
        _contextMenu.Opening += (_, _) =>
        {
            _coordinator.RefreshStatusFlags();
            UpdateMenuState();
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "LedVisual",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };
        _notifyIcon.MouseUp += NotifyIcon_MouseUp;
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();

        _coordinator.StateChanged += UpdateMenuState;
        UpdateMenuState();
    }

    private void NotifyIcon_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        // Open context menu on left click as well, matching macOS menu bar UX
        var showMenuMethod = typeof(NotifyIcon).GetMethod(
            "ShowContextMenu",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (showMenuMethod is not null)
        {
            showMenuMethod.Invoke(_notifyIcon, null);
        }
        else
        {
            _contextMenu.Show(Cursor.Position);
        }
    }

    private void UpdateMenuState()
    {
        var hotkeyText = _coordinator.Settings.Hotkey.DisplayString;
        _statusItem.Text = $"LedVisual — {_coordinator.Status.Label} [{hotkeyText}]";

        _setupWarningItem.Visible = !_coordinator.HasApiKey;
        _captureNowItem.Enabled = _coordinator.HasApiKey;

        _lastActivityItem.Text = $"Last: {Truncate(_coordinator.LastSummary, 64)}";

        if (!string.IsNullOrWhiteSpace(_coordinator.LastError))
        {
            _lastErrorItem.Visible = true;
            _lastErrorItem.Text = $"Error: {Truncate(_coordinator.LastError, 64)}";
        }
        else
        {
            _lastErrorItem.Visible = false;
        }

        // NotifyIcon.Text is limited to 63 characters in WinForms
        var tooltip = $"LedVisual ({_coordinator.Status.Label})";
        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized)
            {
                _settingsForm.WindowState = FormWindowState.Normal;
            }

            _settingsForm.BringToFront();
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_coordinator);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void QuitApplication()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Close();
        }

        _coordinator.Shutdown();
        _notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coordinator.StateChanged -= UpdateMenuState;
            _coordinator.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
            _trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 1)] + "…";
    }

    /// <summary>
    /// Draws a discreet circular icon (matching macOS "circle.inset.filled") without external assets.
    /// </summary>
    private static Icon CreateDiscreetTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var ringPen = new Pen(Color.FromArgb(235, 235, 235), 2.8f);
            g.DrawEllipse(ringPen, 3.5f, 3.5f, 25f, 25f);

            using var centerBrush = new SolidBrush(Color.FromArgb(235, 235, 235));
            g.FillEllipse(centerBrush, 10.5f, 10.5f, 11f, 11f);
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            _ = DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);
}
