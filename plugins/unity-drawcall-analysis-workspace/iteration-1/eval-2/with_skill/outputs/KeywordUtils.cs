#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UTDrawcallAnalysis
{
    /// <summary>
    /// Utility class for shader keyword normalisation: trimming, sorting,
    /// deduplication, and deterministic key generation.
    /// <para>
    /// All methods are null-safe and return stable, predictable results
    /// suitable for use as dictionary keys or comparison tokens.
    /// </para>
    /// </summary>
    public static class KeywordUtils
    {
        /// <summary>
        /// Normalise a sequence of raw keyword strings:
        /// <list type="bullet">
        ///   <item>Trim leading/trailing whitespace from each keyword.</item>
        ///   <item>Exclude null, empty, or whitespace-only entries.</item>
        ///   <item>Remove duplicates (case-sensitive ordinal comparison).</item>
        ///   <item>Sort results alphabetically (ordinal).</item>
        /// </list>
        /// </summary>
        /// <param name="keywords">Raw keyword enumeration. May be null.</param>
        /// <returns>A cleaned, sorted, deduplicated array. Never null.</returns>
        public static string[] NormalizeKeywords(IEnumerable<string> keywords)
        {
            if (keywords == null)
                return Array.Empty<string>();

            return keywords
                .Select(k => k?.Trim() ?? string.Empty)
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Compute a deterministic single-string key from an array of normalised keywords,
        /// using a comma separator.  Useful as a hash-key for quick deduplication.
        /// <para>
        /// Pre-condition: the caller should pass already-normalised keywords.
        /// If the array is null or empty, an empty string is returned.
        /// </para>
        /// </summary>
        public static string GetKeywordsKey(string[] keywords)
        {
            if (keywords == null || keywords.Length == 0)
                return string.Empty;

            return string.Join(",", keywords);
        }

        /// <summary>
        /// Generate a unique, deterministic variant key combining shader name, pass index,
        /// and normalised keywords.  The key is a pipe-delimited string:
        /// <c>shaderName|passIndex|kw1,kw2,...</c>
        /// <para>
        /// The keywords are normalised internally before building the key.
        /// </para>
        /// </summary>
        public static string GetVariantKey(string shaderName, int passIndex, string[] keywords)
        {
            var sb = new StringBuilder();
            sb.Append(shaderName ?? string.Empty);
            sb.Append('|');
            sb.Append(passIndex);
            sb.Append('|');
            sb.Append(GetKeywordsKey(NormalizeKeywords(keywords)));
            return sb.ToString();
        }

        /// <summary>
        /// Order-insensitive comparison of two keyword arrays.
        /// Both arrays are treated as sets; duplicates within a single array
        /// are disregarded.
        /// </summary>
        public static bool KeywordsEqual(string[] a, string[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;

            var setA = new HashSet<string>(a, StringComparer.Ordinal);
            var setB = new HashSet<string>(b, StringComparer.Ordinal);

            if (setA.Count != setB.Count) return false;
            return setA.SetEquals(setB);
        }
    }
}

#endif // UNITY_EDITOR
