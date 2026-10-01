using System.Globalization;
using System.Text;

namespace Steamy.Services;

public sealed record GameSearchResult<T>(IReadOnlyList<T> Items, bool IsTypoMatch);

/// <summary>
/// Immutable, reusable search data. Names are normalized once per catalog, rather than once per
/// character typed. Approximate matching runs only when exact matching finds nothing and examines
/// a bounded vocabulary of similarly shaped words; it never compares every title by edit distance.
/// </summary>
public sealed class GameSearchIndex<T>
{
    private const int MaxFuzzyWords = 6;
    private const int MaxWordCandidates = 4_000;
    private const int MaxSimilarWords = 48;
    private readonly Entry[] _entries;
    private readonly Dictionary<string, List<int>> _wordItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _wordBuckets = new(StringComparer.Ordinal);

    private sealed record Entry(T Item, string AppId, string Name);

    public GameSearchIndex(IEnumerable<T> items, Func<T, int> appId, Func<T, string> name)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(appId);
        ArgumentNullException.ThrowIfNull(name);
        _entries = items.Select(item => new Entry(item,
            appId(item).ToString(CultureInfo.InvariantCulture), Normalize(name(item)))).ToArray();
        for (var i = 0; i < _entries.Length; i++)
        {
            foreach (var word in Words(_entries[i].Name).Distinct(StringComparer.Ordinal))
            {
                if (!_wordItems.TryGetValue(word, out var ids))
                {
                    ids = new List<int>();
                    _wordItems[word] = ids;
                    if (word.Length is >= 4 and <= 32)
                        foreach (var key in BucketKeys(word))
                        {
                            if (!_wordBuckets.TryGetValue(key, out var words))
                                _wordBuckets[key] = words = new List<string>();
                            words.Add(word);
                        }
                }
                ids.Add(i);
            }
        }
    }

    public GameSearchResult<T> Find(string? query, CancellationToken cancellationToken = default,
        Func<T, bool>? eligible = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var search = Normalize(query?.Trim() ?? string.Empty);
        var numeric = search.Length > 0 && search.All(char.IsDigit);
        var terms = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var exact = new List<T>();
        foreach (var entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (eligible is not null && !eligible(entry.Item)) continue;
            if (terms.Length == 0 || (numeric
                    ? entry.Name.Contains(search, StringComparison.Ordinal) || entry.AppId.Contains(search, StringComparison.Ordinal)
                    : terms.All(term => entry.Name.Contains(term, StringComparison.Ordinal))))
                exact.Add(entry.Item);
        }
        if (exact.Count > 0 || numeric || terms.Length is 0 or > MaxFuzzyWords || search.Length > 128)
            return new(exact, false);

        var similar = terms.Select(term => SimilarWords(term, cancellationToken)).ToArray();
        var candidates = new HashSet<int>();
        foreach (var words in similar)
            foreach (var word in words)
                foreach (var index in _wordItems[word]) candidates.Add(index);

        var fuzzy = new List<T>();
        foreach (var index in candidates.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _entries[index];
            if (eligible is not null && !eligible(entry.Item)) continue;
            var words = Words(entry.Name).ToHashSet(StringComparer.Ordinal);
            var changedTerms = 0;
            var matches = true;
            for (var i = 0; i < terms.Length; i++)
            {
                if (entry.Name.Contains(terms[i], StringComparison.Ordinal)) continue;
                if (++changedTerms > 2 || !similar[i].Any(words.Contains)) { matches = false; break; }
            }
            if (matches) fuzzy.Add(entry.Item);
        }
        return new(fuzzy, fuzzy.Count > 0);
    }

    private IReadOnlyList<string> SimilarWords(string term, CancellationToken cancellationToken)
    {
        if (term.Length is < 4 or > 32 || term.Any(character => !char.IsLetterOrDigit(character)))
            return Array.Empty<string>();
        var checkedWords = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<string>();
        foreach (var key in BucketKeys(term))
        {
            if (!_wordBuckets.TryGetValue(key, out var candidates)) continue;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!checkedWords.Add(candidate)) continue;
                if (IsOneEditAway(term, candidate)) matches.Add(candidate);
                if (checkedWords.Count >= MaxWordCandidates || matches.Count >= MaxSimilarWords) return matches;
            }
        }
        return matches;
    }

    private static IEnumerable<string> BucketKeys(string word)
    {
        yield return "p:" + word[..2];
        yield return "s:" + word[^2..];
        yield return $"e:{word[0]}:{word[^1]}";
    }

    private static bool IsOneEditAway(string first, string second)
    {
        if (Math.Abs(first.Length - second.Length) > 1) return false;
        if (first.Length == second.Length)
        {
            var mismatch = -1;
            for (var i = 0; i < first.Length; i++)
            {
                if (first[i] == second[i]) continue;
                if (mismatch < 0) { mismatch = i; continue; }
                return i == mismatch + 1 && first[mismatch] == second[i] && first[i] == second[mismatch]
                    && first.AsSpan(i + 1).SequenceEqual(second.AsSpan(i + 1));
            }
            return true;
        }
        if (first.Length > second.Length) (first, second) = (second, first);
        var skipped = false;
        for (int shortIndex = 0, longIndex = 0; shortIndex < first.Length; shortIndex++, longIndex++)
        {
            if (first[shortIndex] == second[longIndex]) continue;
            if (skipped) return false;
            skipped = true;
            longIndex++;
            if (first[shortIndex] != second[longIndex]) return false;
        }
        return true;
    }

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(text.Length);
        foreach (var character in text)
            // Treat punctuation as a spelling separator, without requiring the user to type
            // trademark apostrophes or hyphens (Assassin's / Spider-Man / Counter-Strike).
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark
                && !char.IsPunctuation(character))
                result.Append(char.ToLowerInvariant(character));
        return result.ToString().Normalize(NormalizationForm.FormC);
    }

    private static IEnumerable<string> Words(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && char.IsLetterOrDigit(text[i])) { if (start < 0) start = i; continue; }
            if (start < 0) continue;
            yield return text[start..i];
            start = -1;
        }
    }
}
