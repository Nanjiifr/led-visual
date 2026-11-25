using System.Text.Json.Serialization;

namespace LedVisual.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HotkeyKind
{
    KeyCombo,
    ModifierOnly
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
    Win = 1 << 3
}

public static class HotkeyModifiersExtensions
{
    public static string ToPrefixString(this HotkeyModifiers modifiers)
    {
        if (modifiers == HotkeyModifiers.None)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        return string.Join("+", parts) + "+";
    }
}

public sealed record Hotkey
{
    public const int VkCapital = 0x14;
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;

    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;

    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;

    public const int VkLShift = 0xA0;
    public const int VkRShift = 0xA1;
    public const int VkLControl = 0xA2;
    public const int VkRControl = 0xA3;
    public const int VkLMenu = 0xA4;
    public const int VkRMenu = 0xA5;

    private static readonly HashSet<int> ModifierVirtualKeyCodes =
    [
        VkShift,
        VkControl,
        VkMenu,
        VkLShift,
        VkRShift,
        VkLControl,
        VkRControl,
        VkLMenu,
        VkRMenu,
        VkLWin,
        VkRWin
    ];

    public int VirtualKeyCode { get; init; }
    public HotkeyModifiers Modifiers { get; init; }
    public HotkeyKind Kind { get; init; }

    [JsonConstructor]
    public Hotkey(
        int virtualKeyCode,
        HotkeyModifiers modifiers = HotkeyModifiers.None,
        HotkeyKind kind = HotkeyKind.KeyCombo)
    {
        VirtualKeyCode = virtualKeyCode;
        Modifiers = kind == HotkeyKind.ModifierOnly ? HotkeyModifiers.None : modifiers;
        Kind = kind;
    }

    /// <summary>
    /// Default hotkey: Alt+Space.
    /// </summary>
    public static Hotkey Default => new(VkSpace, HotkeyModifiers.Alt, HotkeyKind.KeyCombo);

    /// <summary>
    /// Caps Lock is disallowed as a hotkey because it is used as the LED output indicator.
    /// </summary>
    public static bool IsCapsLock(int virtualKeyCode) => virtualKeyCode == VkCapital;

    public static bool IsModifierKey(int virtualKeyCode) => ModifierVirtualKeyCodes.Contains(virtualKeyCode);

    public static HotkeyModifiers? GetModifierFlag(int virtualKeyCode) => virtualKeyCode switch
    {
        VkControl or VkLControl or VkRControl => HotkeyModifiers.Control,
        VkMenu or VkLMenu or VkRMenu => HotkeyModifiers.Alt,
        VkShift or VkLShift or VkRShift => HotkeyModifiers.Shift,
        VkLWin or VkRWin => HotkeyModifiers.Win,
        _ => null
    };

    /// <summary>
    /// Whether a key-down event matches this hotkey (for <see cref="HotkeyKind.KeyCombo"/> hotkeys).
    /// </summary>
    public bool MatchesKeyDown(int virtualKeyCode, HotkeyModifiers activeModifiers)
    {
        if (Kind != HotkeyKind.KeyCombo || IsCapsLock(virtualKeyCode))
        {
            return false;
        }

        return VirtualKeyCode == virtualKeyCode && Modifiers == activeModifiers;
    }

    /// <summary>
    /// Human-readable representation such as "Alt+Space", "F8", or "Right Alt".
    /// </summary>
    [JsonIgnore]
    public string DisplayString => Kind switch
    {
        HotkeyKind.ModifierOnly => GetKeyName(VirtualKeyCode),
        _ => Modifiers.ToPrefixString() + GetKeyName(VirtualKeyCode)
    };

    public static string GetKeyName(int vkCode)
    {
        if (vkCode is >= 0x30 and <= 0x39)
        {
            return ((char)vkCode).ToString();
        }

        if (vkCode is >= 0x41 and <= 0x5A)
        {
            return ((char)vkCode).ToString();
        }

        if (vkCode is >= 0x70 and <= 0x87)
        {
            return $"F{vkCode - 0x6F}";
        }

        if (vkCode is >= 0x60 and <= 0x69)
        {
            return $"Num {vkCode - 0x60}";
        }

        return vkCode switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x13 => "Pause",
            VkCapital => "Caps Lock",
            VkEscape => "Esc",
            VkSpace => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "Print Screen",
            0x2D => "Insert",
            0x2E => "Delete",
            VkLWin => "Left Win",
            VkRWin => "Right Win",
            0x5D => "Apps",
            0x6A => "Num *",
            0x6B => "Num +",
            0x6D => "Num -",
            0x6E => "Num .",
            0x6F => "Num /",
            0x90 => "Num Lock",
            0x91 => "Scroll Lock",
            VkShift => "Shift",
            VkControl => "Ctrl",
            VkMenu => "Alt",
            VkLShift => "Left Shift",
            VkRShift => "Right Shift",
            VkLControl => "Left Ctrl",
            VkRControl => "Right Ctrl",
            VkLMenu => "Left Alt",
            VkRMenu => "Right Alt",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"Key(0x{vkCode:X2})"
        };
    }
}
