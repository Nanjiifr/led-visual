using System.Text.Json;
using LedVisual.Core.Models;
using Xunit;

namespace LedVisual.Core.Tests;

public sealed class HotkeyTests
{
    [Fact]
    public void DefaultHotkeyMatchesAltSpaceOnly()
    {
        var hotkey = Hotkey.Default;
        Assert.Equal("Alt+Space", hotkey.DisplayString);
        Assert.True(hotkey.MatchesKeyDown(Hotkey.VkSpace, HotkeyModifiers.Alt));
        Assert.False(hotkey.MatchesKeyDown(Hotkey.VkSpace, HotkeyModifiers.None));
        Assert.False(hotkey.MatchesKeyDown(Hotkey.VkSpace, HotkeyModifiers.Alt | HotkeyModifiers.Control));
        Assert.False(hotkey.MatchesKeyDown(0x41, HotkeyModifiers.Alt));
    }

    [Fact]
    public void SingleFunctionKeyMatchesWithoutModifiers()
    {
        const int vkF8 = 0x77;
        var f8 = new Hotkey(vkF8, HotkeyModifiers.None, HotkeyKind.KeyCombo);
        Assert.Equal("F8", f8.DisplayString);
        Assert.True(f8.MatchesKeyDown(vkF8, HotkeyModifiers.None));
        Assert.False(f8.MatchesKeyDown(vkF8, HotkeyModifiers.Win));
    }

    [Fact]
    public void StandaloneModifierHotkeySerializesCleanly()
    {
        var rightAlt = new Hotkey(Hotkey.VkRMenu, HotkeyModifiers.None, HotkeyKind.ModifierOnly);
        Assert.Equal("Right Alt", rightAlt.DisplayString);
        Assert.True(Hotkey.IsModifierKey(Hotkey.VkRMenu));
        Assert.Equal(HotkeyModifiers.Alt, Hotkey.GetModifierFlag(Hotkey.VkRMenu));

        var json = JsonSerializer.Serialize(rightAlt);
        var decoded = JsonSerializer.Deserialize<Hotkey>(json);
        Assert.Equal(rightAlt, decoded);
    }

    [Fact]
    public void CapsLockIsBlocked()
    {
        Assert.True(Hotkey.IsCapsLock(Hotkey.VkCapital));
        var caps = new Hotkey(Hotkey.VkCapital, HotkeyModifiers.None, HotkeyKind.KeyCombo);
        Assert.False(caps.MatchesKeyDown(Hotkey.VkCapital, HotkeyModifiers.None));
    }
}
