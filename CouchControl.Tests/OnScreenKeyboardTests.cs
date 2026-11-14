using CouchControl;
using Xunit;

namespace CouchControl.Tests;

public class OnScreenKeyboardTests
{
    [Fact]
    public void AllRows_HaveTheSameWidth()
    {
        foreach (var page in OnScreenKeyboardModel.Pages)
            foreach (var row in page)
                Assert.Equal(OnScreenKeyboardModel.RowUnits, row.Sum(k => k.Width), 3);
    }

    [Fact]
    public void Layout_FollowsTabTip()
    {
        Assert.Equal(13, OnScreenKeyboardModel.RowUnits);
        var letters = OnScreenKeyboardModel.Pages[0];
        Assert.Equal(OskAction.Esc, letters[0][0].Action);
        Assert.Equal(OskAction.Backspace, letters[0][^1].Action);
        Assert.Equal(OskAction.Tab, letters[1][0].Action);
        Assert.Equal(OskAction.Enter, letters[1][^1].Action);
        Assert.Equal(OskAction.Shift, letters[2][0].Action);
        Assert.Equal(OskAction.Shift, letters[2][^1].Action);
        Assert.Equal(OskAction.Language, letters[3][^1].Action);
        Assert.Equal('1', letters[0][1].Alt); // q
    }

    [Fact]
    public void Pages_HaveTheSameNumberOfRows()
    {
        foreach (var page in OnScreenKeyboardModel.Pages)
            Assert.Equal(OnScreenKeyboardModel.Pages[0].Length, page.Length);
    }

    [Fact]
    public void SymbolPages_TypeEveryPrintableAsciiSymbol()
    {
        var typed = OnScreenKeyboardModel.Pages.SelectMany(p => p).SelectMany(r => r)
            .Where(k => k.Action == OskAction.Char).SelectMany(k => new[] { k.Ch, k.Alt }).ToHashSet();
        foreach (char c in "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~")
            Assert.Contains(c, typed);
    }
}
