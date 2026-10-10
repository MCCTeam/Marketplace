using DMCBK.OfficialPlugins.McpServer;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>The observed-state rings: caps, ordering, cursors, filters, and session clearing.</summary>
public sealed class ObservedStateStoreTests
{
    [Fact]
    public void Chat_CapsAtMaxEntries()
    {
        var store = new ObservedStateStore();
        for (int i = 0; i < ObservedStateStore.MaxEntries + 50; i++)
            store.AddChat("chat", $"line {i}", "s", $"line {i}");

        (IReadOnlyList<McpChatEntry> entries, long latest) = store.GetChat(10000);
        Assert.Equal(ObservedStateStore.MaxEntries, entries.Count);
        Assert.Equal(ObservedStateStore.MaxEntries + 50, latest);
        Assert.Equal("line 50", entries[0].Text);
    }

    [Fact]
    public void Events_FilterAfterIdAndType()
    {
        var store = new ObservedStateStore();
        store.AddEvent("death");
        store.AddEvent("inventory_open", new { inventoryId = 3 });
        store.AddEvent("death");

        (IReadOnlyList<McpEventEntry> all, long latest) = store.GetEvents(0, 50, null);
        Assert.Equal(3, all.Count);
        Assert.Equal(3, latest);

        (IReadOnlyList<McpEventEntry> after, _) = store.GetEvents(1, 50, null);
        Assert.Equal(2, after.Count);
        Assert.All(after, e => Assert.True(e.Id > 1));

        (IReadOnlyList<McpEventEntry> deaths, _) = store.GetEvents(0, 50, "DEATH");
        Assert.Equal(2, deaths.Count);
    }

    [Fact]
    public void Events_ClampMaxCount()
    {
        var store = new ObservedStateStore();
        store.AddEvent("a");
        store.AddEvent("b");
        (IReadOnlyList<McpEventEntry> entries, _) = store.GetEvents(0, 10000, null);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Clear_RestartsIdsAndTime()
    {
        var store = new ObservedStateStore();
        store.AddChat("chat", "hi", "s", "hi");
        store.AddEvent("death");
        store.SetTime(10, 20);
        store.Clear();

        (IReadOnlyList<McpChatEntry> chat, long chatLatest) = store.GetChat(10);
        (IReadOnlyList<McpEventEntry> events, long eventLatest) = store.GetEvents(0, 10, null);
        Assert.Empty(chat);
        Assert.Empty(events);
        Assert.Equal(0, chatLatest);
        Assert.Equal(0, eventLatest);
        Assert.Null(store.GetTime());

        store.AddEvent("respawn");
        (_, long next) = store.GetEvents(0, 10, null);
        Assert.Equal(1, next);
    }

    [Fact]
    public void Time_RoundTrips()
    {
        var store = new ObservedStateStore();
        Assert.Null(store.GetTime());
        store.SetTime(100, 6000);
        Assert.Equal((100L, 6000L), store.GetTime());
    }
}
