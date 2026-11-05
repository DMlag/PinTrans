using AiPhoto.Core.Hotkeys;

namespace AiPhoto.Core.Tests.Hotkeys;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("F1", HotkeyModifiers.None, "F1")]
    [InlineData("Ctrl+F1", HotkeyModifiers.Ctrl, "F1")]
    [InlineData("Ctrl+Shift+A", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, "A")]
    [InlineData("Ctrl+Alt+Shift+Win+PrintScreen", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win, "PrintScreen")]
    [InlineData("Alt+D1", HotkeyModifiers.Alt, "D1")]
    public void Parse_Format_RoundTrip(string text, HotkeyModifiers mods, string key)
    {
        var g = HotkeyGesture.Parse(text);
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(key, g.Key);
        Assert.False(g.IsEmpty);
        Assert.Equal(text, g.ToString());
        Assert.Equal(g, HotkeyGesture.Parse(g.ToString()));
    }

    [Theory]
    [InlineData(" shift + control + f2 ", "Ctrl+Shift+f2")]
    [InlineData("WIN+alt+X", "Alt+Win+X")]
    [InlineData("Meta+Windows+Q", "Win+Q")]
    [InlineData("Ctrl++F3", "Ctrl+F3")]
    public void Parse_IsLenientAndFormatIsCanonical(string text, string canonical)
    {
        Assert.Equal(canonical, HotkeyGesture.Parse(text).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_IsNone(string? text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(HotkeyGesture.None, g);
        Assert.True(g.IsEmpty);
        Assert.Equal("", g.ToString());
        Assert.Equal(HotkeyGesture.None, HotkeyGesture.Parse(text));
    }

    [Theory]
    [InlineData("A+B")]
    [InlineData("Ctrl+F1+F2")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("+")]
    public void Invalid_FailsToParse(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(HotkeyGesture.None, g);
        Assert.Throws<FormatException>(() => HotkeyGesture.Parse(text));
    }

    [Fact]
    public void EmptyKeyWithModifiers_FormatsAsEmpty()
    {
        Assert.Equal("", new HotkeyGesture(HotkeyModifiers.Ctrl, "").ToString());
    }
}
