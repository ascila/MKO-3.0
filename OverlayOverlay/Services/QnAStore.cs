using System.Collections.Generic;
using System.Linq;
using OverlayOverlay.Models;

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
}

