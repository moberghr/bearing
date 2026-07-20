using System.Collections.Generic;
using System.Linq;

namespace Squirrel.App.Input;

/// <summary>
/// Most-recently-used ordering for a set of items (e.g. editor tabs). <see cref="Use"/> moves an item to
/// the front; <see cref="Sync"/> prunes items that no longer exist and appends new ones at the back (least
/// recent). Pure and testable — the Ctrl+Tab cycle state lives in the view.
/// </summary>
public sealed class MruList<T> where T : class
{
    private readonly List<T> _items = new();

    public IReadOnlyList<T> Items => _items;

    public void Use(T item)
    {
        _items.Remove(item);
        _items.Insert(0, item);
    }

    public void Remove(T item) => _items.Remove(item);

    /// <summary>Reconcile with the current set: drop vanished items, append newcomers as least-recent.</summary>
    public void Sync(IEnumerable<T> present)
    {
        var current = present.ToList();
        _items.RemoveAll(x => !current.Contains(x));
        foreach (var x in current)
            if (!_items.Contains(x))
                _items.Add(x);
    }
}
