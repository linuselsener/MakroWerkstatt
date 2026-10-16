using MakroWerkstatt;
using Xunit;

namespace MakroWerkstatt.Tests;

[Collection("SerialTests")]
public sealed class GlobalHotkeyTests
{
    [Theory]
    [InlineData("F1", 0x70, HotkeyModifiers.None, "F1")]
    [InlineData("ctrl+alt+f12", 0x7b, HotkeyModifiers.Control | HotkeyModifiers.Alt, "Ctrl + Alt + F12")]
    [InlineData("Control + Shift + F3", 0x72, HotkeyModifiers.Control | HotkeyModifiers.Shift, "Ctrl + Shift + F3")]
    public void Valid_hotkeys_parse_and_format(string value, int virtualKey, HotkeyModifiers modifiers, string formatted)
    {
        Assert.True(GlobalHotkey.TryParse(value, out var hotkey));
        Assert.Equal(new GlobalHotkey(virtualKey, modifiers), hotkey);
        Assert.Equal(formatted, hotkey!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("F0")]
    [InlineData("F13")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+F1")]
    [InlineData("Win+F9")]
    [InlineData("Ctrl+F1+F2")]
    [InlineData("F1+Ctrl")]
    public void Invalid_hotkeys_are_rejected(string? value)
    {
        Assert.False(GlobalHotkey.TryParse(value, out var hotkey));
        Assert.Null(hotkey);
    }
}
