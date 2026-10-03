using System.Collections.ObjectModel;

namespace CafePos.Core.Common;

/// <summary>
/// Updates an existing <see cref="ObservableCollection{T}"/> in place.
/// Calling Clear() + Add() for every refresh (as the app did) makes the list flicker and
/// resets the scroll position, which is very visible on the order board and the menu.
/// <para>
/// <b>SyncWith NEVER raises <see cref="System.Collections.Specialized.NotifyCollectionChangedAction.Replace"/>,
/// and that is the reason it exists in this shape.</b> Every list in the app that is fed from here is
/// bound through MAUI's BindableLayout, and of its five handlers Replace is the only one that does
/// not re-establish the correspondence between a collection item and a realised child: it indexes
/// into <c>layout.Children[e.OldStartingIndex]</c> and throws
/// <see cref="ArgumentOutOfRangeException"/> when the children are not there yet. Add, Move, Remove
/// and Reset all go through <c>e.Apply(...)</c> — <c>layout.Insert</c>, <c>layout.RemoveAt</c>,
/// <c>CreateChildren()</c> — and cannot produce that shape. So Replace is the canary rather than the
/// root cause, and removing the one place in the app that raises it protects all 21 BindableLayout
/// bindings in <c>Views</c> structurally: no registry, no XAML edits.
/// </para>
/// <para>
/// A row whose key and position both match is therefore refreshed by one of two means, never by
/// <c>SetItem</c>:
/// </para>
/// <list type="bullet">
/// <item>with a <c>refresh</c> delegate, the instance already in the collection is updated and the
/// collection raises NOTHING at all — the cheapest refresh there is, and the only one that needs a
/// row which can be written to;</item>
/// <item>without one, the row is re-created AT THE SAME INDEX (RemoveAt + Insert at that index, not
/// an append). That is the pair of actions BindableLayout applies through <c>layout.RemoveAt</c> /
/// <c>layout.Insert</c>, and it cannot leave a stale row behind whatever the row type is.</item>
/// </list>
/// <para>
/// The re-create branch is the fallback, not the second choice, because the app's rows cannot be
/// updated in place at all: <c>OrderRowViewModel</c> is a <c>Model { get; }</c> with computed
/// properties, so there is nothing on it to write. Forbidding the refresh without offering the
/// fallback would push the burden onto callers reusing an immutable instance, which is the one
/// thing that guarantees a stale row on screen. Re-creating at the same index rules staleness out by
/// construction: whatever is in the collection is what came from the source.
/// </para>
/// <para>
/// <see cref="object.Equals(object)"/> stays the cheap "nothing changed" check ahead of both. For
/// rows with value equality — records, and an <c>ObservableCollection&lt;string&gt;</c> compared by
/// value — it is true and no action is taken at all, which is exactly how those lists behaved before
/// and is deliberately preserved.
/// </para>
/// </summary>
public static class ObservableCollectionSync
{
    /// <summary>
    /// Brings <paramref name="target"/> to the contents of <paramref name="source"/>, matching rows
    /// by <paramref name="keySelector"/> and reporting only Add, Move, Remove and (through
    /// <paramref name="refresh"/>) nothing at all. The collection instance itself is never replaced,
    /// because the binding points at it.
    /// </summary>
    /// <param name="refresh">
    /// Called as <c>refresh(current, incoming)</c> for a row whose key and position both match but
    /// whose value differs, and expected to write the incoming values onto the current instance.
    /// Null (the default) means re-create the row at the same index instead — see the remarks on the
    /// class for why that fallback exists and why the row is re-created rather than replaced.
    /// </param>
    public static void SyncWith<T, TKey>(
        this ObservableCollection<T> target,
        IEnumerable<T> source,
        Func<T, TKey> keySelector,
        Action<T, T>? refresh = null) where TKey : notnull
    {
        var incoming = source.ToList();
        var incomingKeys = incoming.Select(keySelector).ToHashSet();

        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!incomingKeys.Contains(keySelector(target[index]))) target.RemoveAt(index);
        }

        for (var index = 0; index < incoming.Count; index++)
        {
            var item = incoming[index];
            var existingIndex = IndexOf(target, keySelector, keySelector(item));

            if (existingIndex < 0)
            {
                target.Insert(Math.Min(index, target.Count), item);
            }
            else if (existingIndex != index)
            {
                target.Move(existingIndex, index);

                // A Move alone is not enough. Move raises one action and BindableLayout applies it as
                // removeAt + CreateItemView, so the child is rebuilt from the instance the collection
                // now holds — which is the OLD one. Without this branch the incoming instance is
                // discarded and the row silently shows the previous pass's values. It only bites when a
                // row moves UP (existingIndex > index); a row moving down arrives as an Insert above it
                // and is therefore recreated anyway, which is what made this look unreachable.
                //
                // The refresh is applied AFTER the Move, at the destination index, because that is where
                // the stale instance now sits.
                if (!Equals(target[index], item))
                {
                    if (refresh is null)
                    {
                        target.RemoveAt(index);
                        target.Insert(index, item);
                    }
                    else
                    {
                        refresh(target[index], item);
                    }
                }
            }
            else if (!Equals(target[index], item))
            {
                if (refresh is null)
                {
                    // Re-created AT THIS INDEX, never appended: an Add at the end would silently
                    // reorder the list, and every row below this one would then be built against a
                    // neighbour that moved. RemoveAt + Insert is what BindableLayout applies
                    // internally, so the realised children follow the collection.
                    target.RemoveAt(index);
                    target.Insert(index, item);
                }
                else
                {
                    // Zero events. The row already in the collection carries the new values, and
                    // there is nothing for a collection change to announce.
                    refresh(target[index], item);
                }
            }
        }
    }

    private static int IndexOf<T, TKey>(ObservableCollection<T> target, Func<T, TKey> keySelector, TKey key) where TKey : notnull
    {
        for (var index = 0; index < target.Count; index++)
        {
            if (EqualityComparer<TKey>.Default.Equals(keySelector(target[index]), key)) return index;
        }

        return -1;
    }
}