using System.Drawing;
using System.Runtime.InteropServices;
using LedVisual.App.Controllers;
using LedVisual.Core.Models;

namespace LedVisual.App.Views;

/// <summary>
/// Interactive hotkey recorder control supporting modifier combos, single keys (e.g. F8),
/// and standalone modifiers (e.g. Right Alt). Rejects Caps Lock and cancels on Escape.
/// </summary>
public sealed class HotkeyRecorderButton : UserControl
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    private readonly Button _recordButton;
    private readonly Button _actionButton;
    private readonly Label _hintLabel;
    private readonly LowLevelKeyboardProc _hookCallback;

    private Hotkey _hotkey = Hotkey.Default;
    private bool _isRecording;
    private nint _hookHandle;
    private int? _pendingModifierVk;

    public event Action<Hotkey>? HotkeyChanged;
    public event Action<bool>? RecordingChanged;

    public Hotkey Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            UpdateVisuals();
        }
    }

    public bool IsRecording => _isRecording;

    public HotkeyRecorderButton()
    {
        _hookCallback = HookCallback;

        Height = 56;
        Width = 340;
        Margin = Padding.Empty;

        _recordButton = new Button
        {
            Left = 0,
            Top = 0,
            Width = 220,
            Height = 28,
            Font = new Font("Consolas", 9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };
        _recordButton.Click += (_, _) =>
        {
            if (_isRecording)
            {
                StopRecording();
            }
            else
            {
                StartRecording();
            }
        };

        _actionButton = new Button
        {
            Left = 228,
            Top = 0,
            Width = 76,
            Height = 28,
            Text = "Reset"
        };
        _actionButton.Click += (_, _) =>
        {
            if (_isRecording)
            {
                StopRecording();
            }
            else
            {
                SetHotkey(Hotkey.Default);
                ShowDefaultHint();
            }
        };

        _hintLabel = new Label
        {
            Left = 0,
            Top = 32,
            Width = 340,
            Height = 28,
            AutoSize = false,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 8f),
            ForeColor = SystemColors.GrayText
        };

        Controls.Add(_recordButton);
        Controls.Add(_actionButton);
        Controls.Add(_hintLabel);

        ShowDefaultHint();
        UpdateVisuals();
    }

    public void StopRecording()
    {
        if (_hookHandle != nint.Zero)
        {
            _ = UnhookWindowsHookEx(_hookHandle);
            _hookHandle = nint.Zero;
        }

        _pendingModifierVk = null;
        if (_isRecording)
        {
            _isRecording = false;
            UpdateVisuals();
            RecordingChanged?.Invoke(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        StopRecording();
        base.Dispose(disposing);
    }

    private void StartRecording()
    {
        ShowDefaultHint();
        _pendingModifierVk = null;
        _isRecording = true;
        UpdateVisuals();
        RecordingChanged?.Invoke(true);

        var moduleHandle = GetModuleHandle(null);
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookCallback, moduleHandle, 0);
        if (_hookHandle == nint.Zero)
        {
            StopRecording();
        }
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && _isRecording)
        {
            var msg = (int)wParam;
            var isKeyDown = msg is WmKeyDown or WmSysKeyDown;
            var isKeyUp = msg is WmKeyUp or WmSysKeyUp;

            if (isKeyDown || isKeyUp)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vkCode = (int)info.vkCode;

                if (HandleRecordedKey(vkCode, isKeyDown))
                {
                    return (nint)1;
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool HandleRecordedKey(int vkCode, bool isKeyDown)
    {
        if (Hotkey.IsCapsLock(vkCode))
        {
            if (isKeyDown)
            {
                ShowWarning("Caps Lock cannot be bound because it is used for the LED output.");
            }
            return true;
        }

        if (Hotkey.IsModifierKey(vkCode))
        {
            if (isKeyDown)
            {
                var mods = GlobalHotkeyMonitor.ReadCurrentModifiers();
                var flag = Hotkey.GetModifierFlag(vkCode);
                if (flag.HasValue)
                {
                    mods |= flag.Value;
                }

                // Allow standalone modifier if only this single modifier family is held
                if (CountSetBits((int)mods) <= 1)
                {
                    _pendingModifierVk = vkCode;
                }
                else
                {
                    _pendingModifierVk = null;
                }
            }
            else if (_pendingModifierVk == vkCode)
            {
                // User pressed & released a single standalone modifier key -> bind it!
                var newHotkey = new Hotkey(vkCode, HotkeyModifiers.None, HotkeyKind.ModifierOnly);
                BeginInvoke(() =>
                {
                    SetHotkey(newHotkey);
                    ShowDefaultHint();
                    StopRecording();
                });
            }

            return true;
        }

        if (isKeyDown)
        {
            var mods = GlobalHotkeyMonitor.ReadCurrentModifiers();

            // Escape without modifiers cancels recording
            if (vkCode == Hotkey.VkEscape && mods == HotkeyModifiers.None)
            {
                BeginInvoke(StopRecording);
                return true;
            }

            _pendingModifierVk = null;
            var newHotkey = new Hotkey(vkCode, mods, HotkeyKind.KeyCombo);
            BeginInvoke(() =>
            {
                SetHotkey(newHotkey);
                ShowDefaultHint();
                StopRecording();
            });
            return true;
        }

        return true;
    }

    private void SetHotkey(Hotkey hotkey)
    {
        _hotkey = hotkey;
        UpdateVisuals();
        HotkeyChanged?.Invoke(hotkey);
    }

    private void UpdateVisuals()
    {
        if (_isRecording)
        {
            _recordButton.Text = "● Press any key or combo…";
            _recordButton.ForeColor = Color.DarkRed;
            _actionButton.Text = "Cancel";
            _actionButton.Enabled = true;
        }
        else
        {
            _recordButton.Text = _hotkey.DisplayString;
            _recordButton.ForeColor = SystemColors.ControlText;
            _actionButton.Text = "Reset";
            _actionButton.Enabled = _hotkey != Hotkey.Default;
        }
    }

    private void ShowDefaultHint()
    {
        _hintLabel.ForeColor = SystemColors.GrayText;
        _hintLabel.Text = "Combos (Alt+Space), single keys (F8), or standalone modifiers (Right Alt). Tap = screenshot, Hold = record.";
    }

    private void ShowWarning(string message)
    {
        BeginInvoke(() =>
        {
            _hintLabel.ForeColor = Color.DarkOrange;
            _hintLabel.Text = message;
        });
    }

    private static int CountSetBits(int value)
    {
        var count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }
        return count;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }
}
