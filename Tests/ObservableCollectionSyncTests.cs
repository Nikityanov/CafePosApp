using System.Collections.ObjectModel;
using CafePos.Core.Common;

namespace CafePosApp.Tests;

public class ObservableCollectionSyncTests
{
    private sealed record Row(int Id, string Name);

    [Fact]
    public void SyncWith_updates_the_same_collection_instance()
    {
        var target = new ObservableCollection<Row> { new(1, "a"), new(2, "b") };
        var reference = target;

        target.SyncWith([new Row(2, "b"), new Row(3, "c")], row => row.Id);

        Assert.Same(reference, target);
        Assert.Equal([2, 3], target.Select(row => row.Id));
    }

    [Fact]
    public void SyncWith_reorders_without_recreating_items()
    {
        var first = new Row(1, "a");
        var second = new Row(2, "b");
        var target = new ObservableCollection<Row> { first, second };

        target.SyncWith([second, first], row => row.Id);

        // Same instances, just moved.
        Assert.Same(second, target[0]);
        Assert.Same(first, target[1]);
    }

    [Fact]
    public void SyncWith_replaces_changed_items_with_the_same_key()
    {
        var target = new ObservableCollection<Row> { new(1, "old") };

        target.SyncWith([new Row(1, "new")], row => row.Id);

        Assert.Single(target);
        Assert.Equal("new", target[0].Name);
    }

    [Fact]
    public void SyncWith_handles_empty_source_and_empty_target()
    {
        var target = new ObservableCollection<Row> { new(1, "a") };
        target.SyncWith([], row => row.Id);
        Assert.Empty(target);

        target.SyncWith([new Row(5, "x")], row => row.Id);
        Assert.Single(target);
        Assert.Equal(5, target[0].Id);
    }
}
