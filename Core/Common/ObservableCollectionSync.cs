using System.Collections.ObjectModel;

namespace CafePos.Core.Common;

/// <summary>Updates an existing `ObservableCollection{T}` in place.</summary>
/// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

public static class ObservableCollectionSync
{
    /// <summary>Brings <paramref name="target"/> to the contents of <paramref name="source"/>, matching rows by <paramref name="keySelector"/> and reporting only Add,…</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

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

                /// <summary>A Move alone is not enough.</summary>
                /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

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
                    /// <summary>Re-created AT THIS INDEX, never appended: an Add at the end would silently reorder the list, and every row below this one would then be built against…</summary>
                    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

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
