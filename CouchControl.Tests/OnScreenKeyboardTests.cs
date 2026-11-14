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
    public void Pages_HaveTheSameNumberOfRows()
    {
        Assert.Equal(OnScreenKeyboardModel.Pages[0].Length, OnScreenKeyboardModel.Pages[1].Length);
    }
}
