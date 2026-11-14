using Meowser.Browser.Core.Sessions;

namespace Meowser.Browser.Core.Tests;

public sealed class BrowserTabManagerTests
{
    [Fact]
    public void CloseLastTab_CreatesNewActiveTab()
    {
        var manager = new BrowserTabManager();
        var originalId = manager.ActiveTab.Id;

        Assert.True(manager.Close(originalId));

        Assert.Single(manager.Tabs);
        Assert.NotEqual(originalId, manager.ActiveTab.Id);
        Assert.Equal(0, manager.ActiveIndex);
    }

    [Fact]
    public void CloseActiveTab_SelectsNextTabOrPreviousAtEnd()
    {
        var manager = new BrowserTabManager();
        var first = manager.ActiveTab;
        var second = manager.AddTab();
        var third = manager.AddTab();

        Assert.True(manager.Close(second.Id));
        Assert.Same(third, manager.ActiveTab);

        Assert.True(manager.Close(third.Id));
        Assert.Same(first, manager.ActiveTab);
    }

    [Fact]
    public void CaptureAndRestore_PreservesTabsAndSelectedIndex()
    {
        var manager = new BrowserTabManager();
        manager.SetAddress(manager.ActiveTab.Id, new Uri("https://example.com/"));
        manager.AddTab();
        var snapshot = manager.Capture();

        var restored = new BrowserTabManager(snapshot);

        Assert.Equal(2, restored.Tabs.Count);
        Assert.Equal(1, restored.ActiveIndex);
        Assert.Equal("https://example.com/", restored.Tabs[0].Address?.AbsoluteUri);
    }

    [Fact]
    public void Move_ReordersTabsAndKeepsActiveTabSelected()
    {
        var manager = new BrowserTabManager();
        var first = manager.ActiveTab;
        var second = manager.AddTab();
        var third = manager.AddTab();

        Assert.True(manager.Move(first.Id, 2));

        Assert.Same(second, manager.Tabs[0]);
        Assert.Same(third, manager.Tabs[1]);
        Assert.Same(first, manager.Tabs[2]);
        Assert.Same(third, manager.ActiveTab);
        Assert.Equal(1, manager.ActiveIndex);
    }

    [Fact]
    public void Restore_RejectsNonWebAddresses()
    {
        var restored = new BrowserTabManager(new BrowserSessionSnapshot(
            [new BrowserTabSnapshot("javascript:alert(1)", "Unsafe")], 0));

        Assert.Null(restored.ActiveTab.Address);
    }
}
