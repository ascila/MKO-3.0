using System.Collections.Generic;
using System.Linq;
using OverlayOverlay.Models;
using System;

namespace OverlayOverlay.Services;

public static class QnAStore
{
    private static readonly List<QnA> _items = new();

    public static IReadOnlyList<QnA> GetHistory() => _items.OrderByDescending(x => x.CreatedAt).ToList();

    public static void Add(QnA item) => _items.Insert(0, item);

    public static void Update(long id, System.Action<QnA> patch)
    {
        var it = _items.FirstOrDefault(x => x.Id == id);
        if (it != null)
        {
            patch(it);
            it.UpdatedAt = System.DateTime.UtcNow;
        }
    }

    public static IEnumerable<(string question, string answer, QnAContext context)> GetAnsweredPairs(int max = 10)
    {
        return _items.Where(x => !string.IsNullOrWhiteSpace(x.Answer))
                     .Take(max)
                     .Select(x => (x.Question, x.Answer!, x.Context));
    }

    public static void RemoveWhere(Predicate<QnA> predicate)
    {
        if (predicate == null) return;
        _items.RemoveAll(predicate);
    }

    public static void Clear()
    {
        _items.Clear();
    }
}
