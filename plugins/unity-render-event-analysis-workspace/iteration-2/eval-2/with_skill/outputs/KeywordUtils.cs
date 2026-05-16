using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Keyword normalization utilities for shader variant collection.
///
/// FrameDebugger returns keywords as a space-separated string. This utility
/// normalizes them for stable comparison: trim whitespace, deduplicate, sort
/// alphabetically (Ordinal).
///
/// Why Ordinal sorting? Shader keyword comparison is case-sensitive and
/// culture-insensitive. Ordinal sort gives consistent results across all locales.
/// </summary>
public static class KeywordUtils
{
    /// <summary>
    /// Normalize an enumerable of keyword strings: trim, deduplicate, sort.
    /// Returns a new sorted list.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string> keywords)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (keywords != null)
            foreach (var kw in keywords)
                if (!string.IsNullOrWhiteSpace(kw))
                    set.Add(kw.Trim());

        var result = new List<string>(set);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Normalize a raw keyword string (space/tab/newline/semicolon/comma separated).
    /// Returns a sorted, deduplicated list.
    /// </summary>
    public static List<string> Normalize(string rawKeywords)
    {
        if (string.IsNullOrWhiteSpace(rawKeywords))
            return new List<string>();

        return Normalize(rawKeywords.Split(
            new[] { ' ', '\t', '\r', '\n', ';', ',' },
            StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Convert a keyword enumerable to a stable, sortable key string.
    /// The resulting string is space-joined, sorted, and deduplicated.
    /// Useful for dedup key construction and dictionary lookups.
    /// </summary>
    public static string ToKey(IEnumerable<string> keywords)
    {
        return string.Join(" ", Normalize(keywords));
    }

    /// <summary>
    /// Convert a raw keyword string to a stable key string.
    /// </summary>
    public static string ToKey(string rawKeywords)
    {
        return ToKey(Normalize(rawKeywords));
    }

    /// <summary>
    /// Check if two keyword collections are equivalent (same set, order-independent).
    /// </summary>
    public static bool AreEquivalent(IEnumerable<string> a, IEnumerable<string> b)
    {
        var normalizedA = Normalize(a);
        var normalizedB = Normalize(b);

        if (normalizedA.Count != normalizedB.Count) return false;
        for (int i = 0; i < normalizedA.Count; i++)
            if (!string.Equals(normalizedA[i], normalizedB[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>
    /// Check if two raw keyword strings are equivalent (same set, order-independent).
    /// </summary>
    public static bool AreEquivalent(string rawA, string rawB)
    {
        return AreEquivalent(Normalize(rawA), Normalize(rawB));
    }
}
