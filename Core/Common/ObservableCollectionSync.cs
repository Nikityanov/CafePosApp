using System.Collections.ObjectModel;

namespace CafePos.Core.Common;

/// <summary>
/// Updates an existing <see cref="ObservableCollection{T}"/> in place.
/// Calling Clear() + Add() for every refresh (as the app did) makes the list flicker and
/// resets the scroll position, which is very visible on the order board and the menu.
/// </summary>
public static class ObservableCollectionSync
{
    public static void SyncWith<T, TKey>(
        this ObservableCollection<T> target,
        IEnumerable<T> source,
        Func<T, TKey> keySelector) where TKey : notnull
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
            }
            else if (!Equals(target[index], item))
            {
                // Same position and same key: replace to refresh the displayed values.
                target[index] = item;
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
