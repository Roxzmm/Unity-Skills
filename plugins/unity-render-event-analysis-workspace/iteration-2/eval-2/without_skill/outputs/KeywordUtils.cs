#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;

namespace UT.Graphics.ShaderVariantTools
{
    /// <summary>
    /// Utility methods for normalising, sorting, and deduplicating shader keyword strings.
    ///
    /// FrameDebugger reports keywords as a space-separated string, but the order
    /// is not guaranteed to be stable across captures.  KeywordUtils normalises by
    /// sorting alphabetically and removing duplicates so that variants can be
    /// compared and deduplicated reliably.
    /// </summary>
    public static class KeywordUtils
    {
        // -----------------------------------------------------------------
        //  Public API
        // -----------------------------------------------------------------

        /// <summary>
        /// Normalize a space-separated keyword string: trim, deduplicate, and sort
        /// alphabetically (ordinal comparison).  Returns an empty string when input
        /// is null, empty, or contains only whitespace.
        /// </summary>
        public static string NormalizeKeywords(string keywords)
        {
            if (string.IsNullOrWhiteSpace(keywords))
                return string.Empty;

            string[] parts = keywords.Split(s_KeywordSeparators, StringSplitOptions.RemoveEmptyEntries);
            return NormalizeKeywords((IEnumerable<string>)parts);
        }

        /// <summary>
        /// Normalize an enumerable of keyword strings: trim each element, filter
        /// empty entries, deduplicate (ordinal), and sort ascending (ordinal).
        /// </summary>
        public static string NormalizeKeywords(IEnumerable<string> keywords)
        {
            if (keywords == null)
                return string.Empty;

            string[] normalized = keywords
                .Select(k => k?.Trim() ?? string.Empty)
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            return normalized.Length > 0
                ? string.Join(s_KeywordSeparatorStr, normalized)
                : string.Empty;
        }

        /// <summary>
        /// Build a stable composite key from a list of keywords that can be used
        /// for dictionary / hash-set deduplication.
        /// </summary>
        public static string ToKey(IEnumerable<string> keywords)
        {
            return NormalizeKeywords(keywords);
        }

        /// <summary>
        /// Parse a normalised keyword string back into a string array.
        /// Returns an empty array for null/empty/whitespace input.
        /// </summary>
        public static string[] ParseKeywords(string keywords)
        {
            if (string.IsNullOrWhiteSpace(keywords))
                return Array.Empty<string>();

            return keywords.Split(s_KeywordSeparators, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Check whether two keyword strings represent the same set of keywords
        /// (order-independent, case-sensitive comparison).
        /// </summary>
        public static bool AreEquivalent(string keywordsA, string keywordsB)
        {
            return string.Equals(
                NormalizeKeywords(keywordsA),
                NormalizeKeywords(keywordsB),
                StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------
        //  Constants
        // -----------------------------------------------------------------

        /// <summary>
        /// Character used to separate keywords in the normalised string.
        /// </summary>
        public const char KeywordSeparator = ' ';

        private static readonly char[] s_KeywordSeparators = { KeywordSeparator, '\t', '\r', '\n', ';', ',' };

        private static readonly string s_KeywordSeparatorStr = KeywordSeparator.ToString();
    }
}
#endif // UNITY_EDITOR
