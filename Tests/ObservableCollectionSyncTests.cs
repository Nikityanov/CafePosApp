using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CafePos.Core.Common;

namespace CafePosApp.Tests;

public class ObservableCollectionSyncTests
{
    /// <summary>
    /// A row WITH value equality, for the tests about keys and ordering, where equality is exactly
    /// what makes the assertions simple.
    /// </summary>
    private sealed record Row(int Id, string Name);

    /// <summary>
    /// A row WITHOUT value equality — what the app actually binds. OrderRowViewModel and the rest
    /// are classes, so a reload hands SyncWith a different instance for the same key and Equals is
    /// always false.
    /// <para>
    /// This fixture is the whole point of the suite: the record above CANNOT see the bug. With value
    /// equality Equals says two rows carrying the same values are the same row, so the refresh branch
    /// is reached only by a test that deliberately splits the values — and a suite written that way
    /// passed against an implementation that raised Replace on every single row of every reload.
    /// </para>
    /// </summary>
    private sealed class PlainRow
    {
        public PlainRow(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public int Id { get; }

        /// <summary>Settable because the in-place refresh path has to write it; nothing else mutates a row.</summary>
        public string Name { get; set; }
    }

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
    public void SyncWith_refreshes_changed_items_with_the_same_key()
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

    /// <summary>
    /// The invariant, stated as a test: same key, NO SetItem, and the value the page binds did change.
    /// Both halves are needed — dropping the first would pass an implementation that refreshed
    /// nothing, dropping the second would pass one that refreshed everything.
    /// </summary>
    [Fact]
    public void SyncWith_never_raises_Replace_for_a_row_without_value_equality()
    {
        var target = new ObservableCollection<PlainRow> { new(1, "old") };
        var actions = RecordActions(target);

        target.SyncWith([new PlainRow(1, "new")], row => row.Id);

        Assert.DoesNotContain(NotifyCollectionChangedAction.Replace, actions);
        Assert.Equal("new", target[0].Name);
    }

    /// <summary>
    /// Catches the tempting re-implementation "remove the stale row and Add the fresh one", which
    /// refreshes the value correctly and quietly moves the row to the bottom of the list.
    /// </summary>
    [Fact]
    public void SyncWith_keeps_the_row_at_its_index_when_a_row_is_refreshed()
    {
        var target = new ObservableCollection<PlainRow> { new(1, "a"), new(2, "b"), new(3, "c") };

        target.SyncWith([new PlainRow(1, "A"), new PlainRow(2, "b"), new PlainRow(3, "C")], row => row.Id);

        Assert.Equal([1, 2, 3], target.Select(row => row.Id));
    }

    [Fact]
    public void SyncWith_refreshes_in_place_when_a_refresh_action_is_supplied()
    {
        var row = new PlainRow(1, "old");
        var target = new ObservableCollection<PlainRow> { row };
        var actions = RecordActions(target);

        target.SyncWith([new PlainRow(1, "new")], r => r.Id, (current, incoming) => current.Name = incoming.Name);

        // The instance in the collection is the one that was there — nothing was re-created.
        Assert.Same(row, target[0]);
        Assert.Equal("new", target[0].Name);
        Assert.Empty(actions);
    }

    [Fact]
    public void SyncWith_does_not_call_refresh_when_the_row_is_unchanged()
    {
        var row = new PlainRow(1, "same");
        var target = new ObservableCollection<PlainRow> { row };
        var refreshes = 0;

        target.SyncWith([row], r => r.Id, (_, _) => refreshes++);

        Assert.Equal(0, refreshes);
    }

    /// <summary>
    /// THE MOVE-UP CASE, and the one that made this a defect rather than a risk.
    /// <para>
    /// A row that moves DOWN arrives as an Insert above it, so it is re-created and read fresh. A row
    /// that moves UP is a <c>Move</c>: BindableLayout applies that as removeAt + CreateItemView, which
    /// rebuilds the child from the instance the collection still holds — and Move keeps the OLD one,
    /// silently discarding the incoming. Target [a, b, c] against incoming [a, C, b] therefore ended
    /// as [a', c-stale, b'] — the row that jumped up showed the previous pass's values, and nothing
    /// about it looked wrong on screen.
    /// </para>
    /// <para>
    /// It stayed unreachable in the app only because of page-lifecycle reasoning — every mutation of
    /// the order board reloads the list itself — and reasoning like that is not in the code, so the
    /// next action that reorders a list without reloading it would have opened the hole. Hence a test
    /// rather than a note.
    /// </para>
    /// </summary>
    [Fact]
    public void SyncWith_refreshes_a_row_that_moves_up()
    {
        var target = new ObservableCollection<PlainRow> { new(1, "a"), new(2, "b"), new(3, "c") };

        target.SyncWith([new PlainRow(1, "a"), new PlainRow(3, "C"), new PlainRow(2, "b")], row => row.Id);

        Assert.Equal([1, 3, 2], target.Select(row => row.Id));
        // The row that moved UP is the one a Move alone would have left stale.
        Assert.Equal("C", target[1].Name);
    }

    /// <summary>
    /// The same case with a refresh delegate: the row keeps its instance and moves, because that is
    /// the whole point of the delegate on a hot list. The check that matters is that it is refreshed
    /// AT ALL — an in-place refresh that never runs leaves the stale value just as firmly as a Move
    /// that discards the incoming one.
    /// </summary>
    [Fact]
    public void SyncWith_refreshes_a_row_that_moves_up_in_place_when_a_refresh_action_is_supplied()
    {
        var staysAtOne = new PlainRow(1, "a");
        var jumpsToZero = new PlainRow(2, "b");
        var target = new ObservableCollection<PlainRow> { new(3, "c"), staysAtOne, jumpsToZero };
        var actions = RecordActions(target);

        target.SyncWith(
            [new PlainRow(2, "B"), new PlainRow(3, "c"), new PlainRow(1, "a")],
            row => row.Id,
            (current, incoming) => current.Name = incoming.Name);

        // Row 2 jumped to the front and was written to in place: same object, new value.
        Assert.Same(jumpsToZero, target[0]);
        Assert.Equal("B", target[0].Name);
        Assert.Equal([2, 3, 1], target.Select(row => row.Id));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Replace, actions);
    }

    /// <summary>
    /// A row that moves and did not change keeps its instance — the reordering behaviour the whole
    /// method exists for, and the reason the refresh in the Move branch is guarded by an equality
    /// check rather than applied unconditionally.
    /// </summary>
    [Fact]
    public void SyncWith_keeps_the_same_instance_when_a_row_moves_up_unchanged()
    {
        var first = new PlainRow(1, "a");
        var second = new PlainRow(2, "b");
        var target = new ObservableCollection<PlainRow> { first, second };

        target.SyncWith([second, first], row => row.Id);

        Assert.Same(second, target[0]);
        Assert.Same(first, target[1]);
    }

    private static List<NotifyCollectionChangedAction> RecordActions(ObservableCollection<PlainRow> target)
    {
        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, args) => actions.Add(args.Action);
        return actions;
    }
}