#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace UT.Graphics.ShaderVariantTools
{
    // =====================================================================
    //  Data Models
    // =====================================================================

    /// <summary>
    /// Raw draw-call information captured from FrameDebugger.
    /// Populated by iterating FrameDebugger events and extracting
    /// shader, pass index, and keyword data.
    /// </summary>
    public struct DrawCallInfo : IEquatable<DrawCallInfo>
    {
        /// <summary>The shader used by this draw-call.</summary>
        public Shader Shader;
        /// <summary>
        /// Pass index reported by FrameDebugger (global pass index across
        /// all sub-shaders of this shader).
        /// </summary>
        public int PassIndex;
        /// <summary>Space-separated keywords from FrameDebugger (raw, unsorted).</summary>
        public string RawKeywords;
        /// <summary>Pass Name tag from FrameDebugger (may be empty).</summary>
        public string PassName;
        /// <summary>LightMode tag from FrameDebugger (may be empty).</summary>
        public string LightMode;

        public DrawCallInfo(Shader shader, int passIndex, string rawKeywords,
                            string passName = "", string lightMode = "")
        {
            Shader = shader;
            PassIndex = passIndex;
            RawKeywords = rawKeywords ?? string.Empty;
            PassName = passName ?? string.Empty;
            LightMode = lightMode ?? string.Empty;
        }

        public bool Equals(DrawCallInfo other) =>
            Shader == other.Shader &&
            PassIndex == other.PassIndex &&
            RawKeywords == other.RawKeywords &&
            PassName == other.PassName &&
            LightMode == other.LightMode;

        public override bool Equals(object obj) =>
            obj is DrawCallInfo other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + (Shader != null ? Shader.GetHashCode() : 0);
                hash = hash * 23 + PassIndex;
                hash = hash * 23 + (RawKeywords?.GetHashCode() ?? 0);
                hash = hash * 23 + (PassName?.GetHashCode() ?? 0);
                hash = hash * 23 + (LightMode?.GetHashCode() ?? 0);
                return hash;
            }
        }

        public override string ToString() =>
            $"DrawCall [{Shader?.name}] Pass#{PassIndex} " +
            $"Name=\"{PassName}\" LightMode={LightMode} <{RawKeywords}>";
    }

    /// <summary>
    /// A merged, deduplicated shader variant record that combines
    /// FrameDebugger draw-call data with ShaderUtil.GetShaderData pass metadata.
    ///
    /// Each record represents one unique combination of:
    ///   (shader, globalPassIndex, passName, lightMode, normalisedKeywords)
    ///
    /// Equality and hashing are based on the composite dedup key:
    ///   (ShaderPath, PassIndex, PassName, LightMode, NormalizedKeywords)
    ///
    /// IComparable sorts by ShaderPath first, then PassIndex, then
    /// NormalizedKeywords, then PassName, then LightMode — all ordinal.
    /// </summary>
    [Serializable]
    public class ShaderVariantRecord : IComparable<ShaderVariantRecord>
    {
        // ---- Pass identity (enriched from ShaderPassExtractor) ----

        /// <summary>Asset path of the shader (e.g. "Shaders/MyShader.shader").</summary>
        public string ShaderPath;
        /// <summary>Global pass index (flattened across all sub-shaders).</summary>
        public int PassIndex;
        /// <summary>0-based sub-shader index, or -1 if unknown.</summary>
        public int SubShaderIndex;
        /// <summary>0-based pass index within its sub-shader, or -1 if unknown.</summary>
        public int LocalPassIndex;
        /// <summary>The Name tag of the pass (may be empty).</summary>
        public string PassName;
        /// <summary>The LightMode tag of the pass (may be empty).</summary>
        public string LightMode;
        /// <summary>String representation of the PassType enum.</summary>
        public string PassType;

        // ---- Variant data ----

        /// <summary>Normalised, sorted, deduplicated space-separated keywords.</summary>
        public string NormalizedKeywords;

        // ---- Observational metadata (optional enrichment) ----

        /// <summary>Scene paths where this variant was observed.</summary>
        public List<string> ScenePaths = new();

        /// <summary>FrameDebugger event names where this variant was observed.</summary>
        public List<string> FrameEventNames = new();

        // -------------------------------------------------------------
        //  Equality / Hashing / Sorting (composite dedup key)
        // -------------------------------------------------------------
        //
        // The composite dedup key includes:
        //   ShaderPath  |  PassIndex  |  PassName  |  LightMode  |  NormalizedKeywords
        //
        // This ensures that two draw-calls with the same shader and pass
        // index but different PassName / LightMode metadata are treated as
        // distinct records — which is critical when the same global pass
        // index can map to different passes in different sub-shaders.

        public int CompareTo(ShaderVariantRecord other)
        {
            if (other == null) return 1;

            int cmp = string.Compare(ShaderPath, other.ShaderPath, StringComparison.Ordinal);
            if (cmp != 0) return cmp;

            cmp = PassIndex.CompareTo(other.PassIndex);
            if (cmp != 0) return cmp;

            cmp = string.Compare(NormalizedKeywords, other.NormalizedKeywords, StringComparison.Ordinal);
            if (cmp != 0) return cmp;

            cmp = string.Compare(PassName, other.PassName, StringComparison.Ordinal);
            if (cmp != 0) return cmp;

            return string.Compare(LightMode, other.LightMode, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            if (obj is ShaderVariantRecord other)
            {
                return string.Equals(ShaderPath, other.ShaderPath, StringComparison.Ordinal) &&
                       PassIndex == other.PassIndex &&
                       string.Equals(PassName, other.PassName, StringComparison.Ordinal) &&
                       string.Equals(LightMode, other.LightMode, StringComparison.Ordinal) &&
                       string.Equals(NormalizedKeywords, other.NormalizedKeywords, StringComparison.Ordinal);
            }
            return false;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + (ShaderPath?.GetHashCode(StringComparison.Ordinal) ?? 0);
                hash = hash * 23 + PassIndex;
                hash = hash * 23 + (PassName?.GetHashCode(StringComparison.Ordinal) ?? 0);
                hash = hash * 23 + (LightMode?.GetHashCode(StringComparison.Ordinal) ?? 0);
                hash = hash * 23 + (NormalizedKeywords?.GetHashCode(StringComparison.Ordinal) ?? 0);
                return hash;
            }
        }

        public override string ToString() =>
            $"{ShaderPath} | Pass#{PassIndex} (Sub#{SubShaderIndex}/Local#{LocalPassIndex}) " +
            $"\"{PassName}\" {LightMode} [{PassType}] <{NormalizedKeywords}>";
    }

    // =====================================================================
    //  Main Orchestrator
    // =====================================================================

    /// <summary>
    /// Merges FrameDebugger draw-call data with ShaderUtil pass metadata,
    /// deduplicates by (shader, passIndex, passName, lightMode, normalisedKeywords),
    /// sorts, and saves to JSON / CSV / ShaderVariantCollection.
    ///
    /// Typical workflow:
    /// <code>
    ///     // 1. Capture draw-calls from FrameDebugger
    ///     var drawCalls = new List&lt;DrawCallInfo&gt;();
    ///     // ... populate using FrameDebuggerReflect ...
    ///
    ///     // 2. Merge with shader pass metadata
    ///     var records = VariantRecorder.MergeDrawCallsWithPassData(drawCalls);
    ///
    ///     // 3. Export
    ///     VariantRecorder.SaveToJson(records, "Assets/variants.json");
    ///     VariantRecorder.SaveToCsv(records, "Assets/variants.csv");
    ///     VariantRecorder.CreateShaderVariantCollectionAsset(records, "Assets/variants.shadervariants");
    /// </code>
    /// </summary>
    public static class VariantRecorder
    {
        // -----------------------------------------------------------------
        //  Merge
        // -----------------------------------------------------------------

        /// <summary>
        /// Merge FrameDebugger draw-calls with ShaderUtil pass metadata.
        ///
        /// For each draw-call:
        ///   1. Try to resolve the pass metadata via ShaderPassExtractor.ResolveFromDrawCall,
        ///      which attempts FindByIndex (global pass index) first, then falls back to
        ///      FindBestMatch.
        ///   2. If resolution succeeds, enrich the record with sub-shader index, local pass
        ///      index, pass name, light mode, and PassType.
        ///   3. If resolution fails, create a best-effort record with unknown metadata
        ///      (so no draw-call is silently dropped).
        ///
        /// Duplicate records (same shader + passIndex + passName + lightMode + keywords)
        /// are collapsed into a single entry.
        /// </summary>
        /// <param name="drawCalls">Draw-calls collected from FrameDebugger.</param>
        /// <param name="passDataCache">
        /// Optional cache mapping Shader → its pass info list.  Providing a persistent
        /// cache across multiple merge calls avoids repeated ShaderUtil.GetShaderData calls.
        /// </param>
        /// <returns>A sorted, deduplicated list of ShaderVariantRecords.</returns>
        public static List<ShaderVariantRecord> MergeDrawCallsWithPassData(
            IReadOnlyList<DrawCallInfo> drawCalls,
            Dictionary<Shader, List<ShaderPassInfo>> passDataCache = null)
        {
            if (drawCalls == null || drawCalls.Count == 0)
            {
                Debug.Log("[VariantRecorder] No draw-calls to process.");
                return new List<ShaderVariantRecord>();
            }

            bool ownsCache = passDataCache == null;
            if (ownsCache)
                passDataCache = new Dictionary<Shader, List<ShaderPassInfo>>();

            // Collect unique shaders referenced by draw-calls.
            var uniqueShaders = drawCalls
                .Select(d => d.Shader)
                .Where(s => s != null)
                .Distinct()
                .ToList();

            // Extract pass data for shaders not yet in cache.
            foreach (var shader in uniqueShaders)
            {
                if (!passDataCache.ContainsKey(shader))
                {
                    passDataCache[shader] = ShaderPassExtractor.ExtractAllPasses(shader);
                }
            }

            // Merge and deduplicate using HashSet with composite key.
            var recordSet = new Dictionary<string, ShaderVariantRecord>();

            foreach (var drawCall in drawCalls)
            {
                if (drawCall.Shader == null)
                    continue;

                string shaderPath = AssetDatabase.GetAssetPath(drawCall.Shader);
                string normalizedKeywords = KeywordUtils.NormalizeKeywords(drawCall.RawKeywords);

                // Try to resolve pass metadata.
                ShaderPassInfo? resolved = null;
                if (passDataCache.TryGetValue(drawCall.Shader, out var passInfos))
                {
                    resolved = ShaderPassExtractor.ResolveFromDrawCall(
                        drawCall.Shader, drawCall.PassIndex, drawCall.PassName, drawCall.LightMode);
                }

                // Build the composite dedup key: shader|passIndex|passName|lightMode|keywords.
                string passName = resolved?.PassName ?? drawCall.PassName ?? string.Empty;
                string lightMode = resolved?.LightMode ?? drawCall.LightMode ?? string.Empty;
                string passTypeName = resolved.HasValue
                    ? resolved.Value.PassType.ToString()
                    : "Unknown";

                string dedupKey = MakeDedupKey(shaderPath, drawCall.PassIndex,
                    passName, lightMode, normalizedKeywords);

                if (recordSet.TryGetValue(dedupKey, out var existing))
                {
                    // Collapse: merge observational metadata.
                    if (!string.IsNullOrEmpty(drawCall.PassName) &&
                        !existing.FrameEventNames.Contains(drawCall.PassName))
                    {
                        existing.FrameEventNames.Add(drawCall.PassName);
                    }
                    continue;
                }

                var record = new ShaderVariantRecord
                {
                    ShaderPath = shaderPath,
                    PassIndex = drawCall.PassIndex,
                    SubShaderIndex = resolved?.SubShaderIndex ?? -1,
                    LocalPassIndex = resolved?.LocalPassIndex ?? -1,
                    PassName = passName,
                    LightMode = lightMode,
                    PassType = passTypeName,
                    NormalizedKeywords = normalizedKeywords,
                };

                if (!string.IsNullOrEmpty(drawCall.PassName))
                    record.FrameEventNames.Add(drawCall.PassName);

                recordSet[dedupKey] = record;
            }

            // Sort.
            var sorted = recordSet.Values.ToList();
            sorted.Sort();

            Debug.Log($"[VariantRecorder] Merged {drawCalls.Count} draw-calls into {sorted.Count} unique variant records.");
            return sorted;
        }

        /// <summary>
        /// Convenience overload that accepts raw data arrays
        /// (e.g. from FrameDebuggerReflect extraction).
        /// </summary>
        public static List<ShaderVariantRecord> MergeDrawCallsWithPassData(
            Shader[] shaders,
            int[] passIndices,
            string[] rawKeywordsArray,
            string[] passNames = null,
            string[] lightModes = null,
            Dictionary<Shader, List<ShaderPassInfo>> passDataCache = null)
        {
            if (shaders == null || passIndices == null || rawKeywordsArray == null)
            {
                Debug.LogWarning("[VariantRecorder] Input arrays cannot be null.");
                return new List<ShaderVariantRecord>();
            }

            int count = Mathf.Min(shaders.Length, passIndices.Length, rawKeywordsArray.Length);
            var drawCalls = new List<DrawCallInfo>(count);

            for (int i = 0; i < count; i++)
            {
                drawCalls.Add(new DrawCallInfo(
                    shaders[i],
                    passIndices[i],
                    rawKeywordsArray[i],
                    passNames != null && i < passNames.Length ? passNames[i] : null,
                    lightModes != null && i < lightModes.Length ? lightModes[i] : null));
            }

            return MergeDrawCallsWithPassData(drawCalls, passDataCache);
        }

        // -----------------------------------------------------------------
        //  Save Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Save variant records as a pretty-printed JSON file.
        /// </summary>
        /// <param name="records">Sorted, deduplicated records.</param>
        /// <param name="filePath">Absolute or Assets-relative file path ending in ".json".</param>
        public static void SaveToJson(List<ShaderVariantRecord> records, string filePath)
        {
            if (records == null)
                records = new List<ShaderVariantRecord>();

            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var wrapper = new ShaderVariantCollectionWrapper { Records = records };
            string json = JsonUtility.ToJson(wrapper, prettyPrint: true);
            File.WriteAllText(filePath, json, Encoding.UTF8);

            Debug.Log($"[VariantRecorder] Saved {records.Count} variant records to JSON: {filePath}");
        }

        /// <summary>
        /// Save variant records as a CSV file with RFC 4180 escaping.
        /// Columns: ShaderPath, PassIndex, SubShaderIndex, LocalPassIndex,
        ///          PassName, LightMode, PassType, NormalizedKeywords.
        /// </summary>
        public static void SaveToCsv(List<ShaderVariantRecord> records, string filePath)
        {
            if (records == null)
                records = new List<ShaderVariantRecord>();

            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var writer = new StreamWriter(filePath, append: false, Encoding.UTF8))
            {
                writer.WriteLine("ShaderPath,PassIndex,SubShaderIndex,LocalPassIndex,PassName,LightMode,PassType,NormalizedKeywords");

                foreach (var record in records)
                {
                    writer.WriteLine(
                        $"\"{EscapeCsv(record.ShaderPath)}\"," +
                        $"{record.PassIndex}," +
                        $"{record.SubShaderIndex}," +
                        $"{record.LocalPassIndex}," +
                        $"\"{EscapeCsv(record.PassName)}\"," +
                        $"\"{EscapeCsv(record.LightMode)}\"," +
                        $"\"{EscapeCsv(record.PassType)}\"," +
                        $"\"{EscapeCsv(record.NormalizedKeywords)}\"");
                }
            }

            Debug.Log($"[VariantRecorder] Saved {records.Count} variant records to CSV: {filePath}");
        }

        /// <summary>
        /// Export the records as a set of per-shader CSV files, one file per
        /// shader in the given output directory.
        /// </summary>
        /// <param name="records">Records to export.</param>
        /// <param name="outputDir">Directory where per-shader CSV files will be written.</param>
        public static void SavePerShaderCsv(List<ShaderVariantRecord> records, string outputDir)
        {
            if (records == null || records.Count == 0)
                return;

            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            // Group records by shader path.
            var groups = records.GroupBy(r => r.ShaderPath);

            foreach (var group in groups)
            {
                string shaderName = Path.GetFileNameWithoutExtension(group.Key);
                string safeName = SanitizeFileName(shaderName);
                string filePath = Path.Combine(outputDir, $"{safeName}.csv");

                using (var writer = new StreamWriter(filePath, append: false, Encoding.UTF8))
                {
                    writer.WriteLine("PassIndex,SubShaderIndex,LocalPassIndex,PassName,LightMode,PassType,NormalizedKeywords");

                    foreach (var record in group.OrderBy(r => r.PassIndex).ThenBy(r => r.NormalizedKeywords))
                    {
                        writer.WriteLine(
                            $"{record.PassIndex}," +
                            $"{record.SubShaderIndex}," +
                            $"{record.LocalPassIndex}," +
                            $"\"{EscapeCsv(record.PassName)}\"," +
                            $"\"{EscapeCsv(record.LightMode)}\"," +
                            $"\"{EscapeCsv(record.PassType)}\"," +
                            $"\"{EscapeCsv(record.NormalizedKeywords)}\"");
                    }
                }
            }

            Debug.Log($"[VariantRecorder] Saved per-shader CSV files to: {outputDir}");
        }

        /// <summary>
        /// Create a Unity ShaderVariantCollection asset from the records.
        ///
        /// Note: ShaderVariantCollection uses PassType (not passIndex) internally,
        /// so records with different passIndices but the same (shader, PassType, keywords)
        /// will be collapsed into a single entry.  Use SaveToJson or SaveToCsv for
        /// pass-index-precise export.
        /// </summary>
        /// <param name="records">Sorted, deduplicated records.</param>
        /// <param name="assetPath">Assets-relative path ending in ".shadervariants".</param>
        /// <returns>The created ShaderVariantCollection asset, or null on failure.</returns>
        public static ShaderVariantCollection CreateShaderVariantCollectionAsset(
            List<ShaderVariantRecord> records,
            string assetPath)
        {
            if (records == null || records.Count == 0)
            {
                Debug.LogWarning("[VariantRecorder] No records to create ShaderVariantCollection.");
                return null;
            }

            var svc = new ShaderVariantCollection();
            int added = 0;
            int skipped = 0;

            foreach (var record in records)
            {
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(record.ShaderPath);
                if (shader == null)
                {
                    skipped++;
                    continue;
                }

                if (!Enum.TryParse<PassType>(record.PassType, out var passType))
                {
                    Debug.LogWarning($"[VariantRecorder] Unrecognised PassType '{record.PassType}' for {record.ShaderPath}, skipping.");
                    skipped++;
                    continue;
                }

                var variant = new ShaderVariantCollection.ShaderVariant
                {
                    shader = shader,
                    keywords = KeywordUtils.ParseKeywords(record.NormalizedKeywords),
                    passType = passType,
                };

                if (svc.Add(variant))
                    added++;
                else
                    skipped++;
            }

            // Ensure directory exists.
            string dir = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            AssetDatabase.CreateAsset(svc, assetPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[VariantRecorder] Created ShaderVariantCollection at '{assetPath}': " +
                      $"{added} variants added, {skipped} skipped.");

            return svc;
        }

        /// <summary>
        /// Convenience: merge draw-calls, then save all output formats in one call.
        /// </summary>
        /// <returns>The sorted, deduplicated list of records.</returns>
        public static List<ShaderVariantRecord> ProcessAndSave(
            IReadOnlyList<DrawCallInfo> drawCalls,
            string jsonPath = null,
            string csvPath = null,
            string svcAssetPath = null,
            string perShaderCsvDir = null,
            Dictionary<Shader, List<ShaderPassInfo>> passDataCache = null)
        {
            var records = MergeDrawCallsWithPassData(drawCalls, passDataCache);

            if (!string.IsNullOrEmpty(jsonPath))
                SaveToJson(records, jsonPath);

            if (!string.IsNullOrEmpty(csvPath))
                SaveToCsv(records, csvPath);

            if (!string.IsNullOrEmpty(perShaderCsvDir))
                SavePerShaderCsv(records, perShaderCsvDir);

            if (!string.IsNullOrEmpty(svcAssetPath))
                CreateShaderVariantCollectionAsset(records, svcAssetPath);

            return records;
        }

        // -----------------------------------------------------------------
        //  Internal Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Build a deterministic dedup key that includes passName and lightMode
        /// so that two draw-calls with the same passIndex but different pass
        /// identity are treated as distinct records.
        /// </summary>
        private static string MakeDedupKey(
            string shaderPath, int passIndex,
            string passName, string lightMode, string normalizedKeywords)
        {
            // Using a structure that guarantees uniqueness when any component differs.
            return $"{shaderPath}\0{passIndex}\0{passName}\0{lightMode}\0{normalizedKeywords}";
        }

        [Serializable]
        private class ShaderVariantCollectionWrapper
        {
            public List<ShaderVariantRecord> Records;
        }

        private static string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (value.Contains('"') || value.Contains(',') ||
                value.Contains('\n') || value.Contains('\r'))
                return value.Replace("\"", "\"\"");

            return value;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "unnamed";

            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);

            return sb.ToString();
        }
    }
}
#endif // UNITY_EDITOR
