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
    //  Data models
    // =====================================================================

    /// <summary>
    /// Represents a single draw call captured by FrameDebugger.
    /// The caller populates these from their own FrameDebugger iteration logic.
    /// </summary>
    public struct DrawCallInfo : IEquatable<DrawCallInfo>
    {
        /// <summary> The shader used by this draw call. </summary>
        public Shader Shader;
        /// <summary> Pass index reported by FrameDebugger (typically local index within the active sub-shader). </summary>
        public int PassIndex;
        /// <summary> Space-separated keywords reported by FrameDebugger (raw, may be unsorted). </summary>
        public string RawKeywords;

        public DrawCallInfo(Shader shader, int passIndex, string rawKeywords)
        {
            Shader = shader;
            PassIndex = passIndex;
            RawKeywords = rawKeywords ?? string.Empty;
        }

        public bool Equals(DrawCallInfo other) =>
            Shader == other.Shader &&
            PassIndex == other.PassIndex &&
            RawKeywords == other.RawKeywords;

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
                return hash;
            }
        }

        public override string ToString() =>
            $"DrawCall [{Shader?.name}] Pass#{PassIndex} <{RawKeywords}>";
    }

    /// <summary>
    /// A merged, deduplicated shader variant record combining FrameDebugger draw-call
    /// data with ShaderUtil.GetShaderData pass metadata.
    ///
    /// Equality and hashing are based on (ShaderPath, PassIndex, NormalizedKeywords).
    /// The IComparable implementation sorts first by ShaderPath, then PassIndex,
    /// then NormalizedKeywords — all ordinal.
    /// </summary>
    [System.Serializable]
    public class ShaderVariantRecord : IComparable<ShaderVariantRecord>
    {
        // ---- Serialised fields (used for JSON output) ----

        /// <summary> Asset path of the shader (e.g. "Shaders/MyShader.shader"). </summary>
        public string ShaderPath;
        /// <summary> Pass index as reported by FrameDebugger. </summary>
        public int PassIndex;
        /// <summary> 0-based sub-shader index this pass belongs to, or -1 if unknown. </summary>
        public int SubShaderIndex;
        /// <summary> 0-based pass index within its sub-shader, or -1 if unknown. </summary>
        public int LocalPassIndex;
        /// <summary> The Name tag of the pass (may be empty). </summary>
        public string PassName;
        /// <summary> The LightMode tag of the pass (may be empty). </summary>
        public string LightMode;
        /// <summary> String representation of the PassType enum (e.g. "Normal", "ScriptableRenderPipeline"). </summary>
        public string PassType;
        /// <summary> Normalised, sorted, deduplicated space-separated keyword string. </summary>
        public string NormalizedKeywords;

        // ---- Equality / Hashing / Sorting (ignoring metadata beyond identity) ----

        public int CompareTo(ShaderVariantRecord other)
        {
            if (other == null) return 1;

            int cmp = string.Compare(ShaderPath, other.ShaderPath, StringComparison.Ordinal);
            if (cmp != 0) return cmp;

            cmp = PassIndex.CompareTo(other.PassIndex);
            if (cmp != 0) return cmp;

            return string.Compare(NormalizedKeywords, other.NormalizedKeywords, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            if (obj is ShaderVariantRecord other)
            {
                return string.Equals(ShaderPath, other.ShaderPath, StringComparison.Ordinal) &&
                       PassIndex == other.PassIndex &&
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
                hash = hash * 23 + (NormalizedKeywords?.GetHashCode(StringComparison.Ordinal) ?? 0);
                return hash;
            }
        }

        public override string ToString() =>
            $"{ShaderPath} | Pass#{PassIndex} (Sub#{SubShaderIndex}/Local#{LocalPassIndex}) " +
            $"\"{PassName}\" {LightMode} [{PassType}] <{NormalizedKeywords}>";
    }

    // =====================================================================
    //  Main orchestrator
    // =====================================================================

    /// <summary>
    /// Merges FrameDebugger draw-call data with ShaderUtil pass metadata,
    /// deduplicates by (shader, passIndex, normalisedKeywords), sorts, and
    /// saves to JSON / CSV / ShaderVariantCollection.
    ///
    /// Typical usage:
    /// <code>
    ///     // 1. Collect draw-calls from FrameDebugger
    ///     var drawCalls = new List&lt;DrawCallInfo&gt;();
    ///     // ... populate from FrameDebugger ...
    ///
    ///     // 2. Merge &amp; deduplicate
    ///     var records = VariantRecorder.MergeDrawCallsWithPassData(drawCalls);
    ///
    ///     // 3. Save
    ///     VariantRecorder.SaveToJson(records, "Assets/variants.json");
    ///     VariantRecorder.SaveToCsv(records, "Assets/variants.csv");
    /// </code>
    /// </summary>
    public static class VariantRecorder
    {
        // -----------------------------------------------------------------
        //  Merge
        // -----------------------------------------------------------------

        /// <summary>
        /// Merge a list of FrameDebugger draw-calls with ShaderUtil pass metadata.
        ///
        /// Matching strategy:
        ///   1. Try to match drawCall.PassIndex as a local pass index within each sub-shader.
        ///   2. If no local match is found, try matching as a global (flattened) pass index.
        ///   3. If still no match, a record is still created with unavailable metadata
        ///      so that no draw-call is silently dropped.
        ///
        /// An optional passDataCache can be provided to avoid re-extracting pass
        /// data for shaders that have already been processed.
        /// </summary>
        /// <param name="drawCalls"> Draw-calls collected from FrameDebugger. </param>
        /// <param name="passDataCache">
        ///     Optional cache mapping Shader → its pass info list.
        ///     If provided, the method populates it for any new shaders encountered.
        ///     Passing a persistent cache saves repeated ShaderUtil calls.
        /// </param>
        /// <returns>
        ///     A sorted, deduplicated list of ShaderVariantRecords.
        /// </returns>
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

            // Collect all unique shaders referenced by the draw-calls.
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

            // Merge.
            var recordSet = new HashSet<ShaderVariantRecord>();

            foreach (var drawCall in drawCalls)
            {
                if (drawCall.Shader == null)
                    continue;

                if (!passDataCache.TryGetValue(drawCall.Shader, out var passInfos))
                    continue;

                string shaderPath = AssetDatabase.GetAssetPath(drawCall.Shader);
                string normalizedKeywords = KeywordUtils.NormalizeKeywords(drawCall.RawKeywords);

                // Strategy 1: match PassIndex as a local pass index within any sub-shader.
                var matchingPasses = passInfos
                    .Where(p => p.LocalPassIndex == drawCall.PassIndex)
                    .ToList();

                // Strategy 2: if no local match, try matching as a global pass index.
                if (matchingPasses.Count == 0)
                {
                    matchingPasses = passInfos
                        .Where(p => p.GlobalPassIndex == drawCall.PassIndex)
                        .ToList();
                }

                if (matchingPasses.Count == 0)
                {
                    // Create a best-effort record (metadata marked unavailable).
                    recordSet.Add(new ShaderVariantRecord
                    {
                        ShaderPath = shaderPath,
                        PassIndex = drawCall.PassIndex,
                        SubShaderIndex = -1,
                        LocalPassIndex = -1,
                        PassName = string.Empty,
                        LightMode = string.Empty,
                        PassType = "Unknown",
                        NormalizedKeywords = normalizedKeywords,
                    });
                    continue;
                }

                // Create one record per matching pass (usually just one).
                foreach (var passInfo in matchingPasses)
                {
                    recordSet.Add(new ShaderVariantRecord
                    {
                        ShaderPath = shaderPath,
                        PassIndex = drawCall.PassIndex,
                        SubShaderIndex = passInfo.SubShaderIndex,
                        LocalPassIndex = passInfo.LocalPassIndex,
                        PassName = passInfo.PassName,
                        LightMode = passInfo.LightMode,
                        PassType = passInfo.PassType.ToString(),
                        NormalizedKeywords = normalizedKeywords,
                    });
                }
            }

            // Sort.
            var sorted = recordSet.ToList();
            sorted.Sort();

            Debug.Log($"[VariantRecorder] Merged {drawCalls.Count} draw-calls into {sorted.Count} unique variant records.");
            return sorted;
        }

        // -----------------------------------------------------------------
        //  Save helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Save variant records as a pretty-printed JSON file.
        /// </summary>
        /// <param name="records"> Sorted, deduplicated records. </param>
        /// <param name="filePath"> Absolute or Assets-relative file path. Must end in ".json". </param>
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
        /// Save variant records as a CSV file (easy to open in spreadsheets).
        /// </summary>
        /// <param name="records"> Sorted, deduplicated records. </param>
        /// <param name="filePath"> Absolute or Assets-relative file path. Must end in ".csv". </param>
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
        /// Create a Unity ShaderVariantCollection asset from the records.
        /// Note: ShaderVariantCollection uses PassType (not passIndex) for matching,
        /// so records with the same (shader, PassType, keywords) will map to
        /// a single entry even if they have different passIndices.
        /// </summary>
        /// <param name="records"> Sorted, deduplicated records. </param>
        /// <param name="assetPath"> Assets-relative path ending in ".shadervariants". </param>
        /// <returns> The created ShaderVariantCollection asset, or null on failure. </returns>
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

                // Try to parse PassType; default to Normal if parsing fails.
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
                      $"{added} variants added, {skipped} skipped (duplicates / null shaders / bad PassType).");

            return svc;
        }

        /// <summary>
        /// Convenience: merge draw-calls, then save both JSON and CSV in one call.
        /// </summary>
        public static List<ShaderVariantRecord> ProcessAndSave(
            IReadOnlyList<DrawCallInfo> drawCalls,
            string jsonPath,
            string csvPath,
            Dictionary<Shader, List<ShaderPassInfo>> passDataCache = null)
        {
            var records = MergeDrawCallsWithPassData(drawCalls, passDataCache);

            if (!string.IsNullOrEmpty(jsonPath))
                SaveToJson(records, jsonPath);

            if (!string.IsNullOrEmpty(csvPath))
                SaveToCsv(records, csvPath);

            return records;
        }

        // -----------------------------------------------------------------
        //  Internal helpers
        // -----------------------------------------------------------------

        [System.Serializable]
        private class ShaderVariantCollectionWrapper
        {
            public List<ShaderVariantRecord> Records;
        }

        private static string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            // If the value contains a comma, quote, or newline, escape by doubling quotes.
            if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
                return value.Replace("\"", "\"\"");
            return value;
        }
    }
}
#endif // UNITY_EDITOR
