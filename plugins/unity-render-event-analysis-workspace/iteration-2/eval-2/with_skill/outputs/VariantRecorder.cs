using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// ====================================================================
// Data Classes
// ====================================================================

/// <summary>
/// Raw event data extracted from FrameDebuggerEventData via reflection.
/// Used as input to the variant recorder.
/// </summary>
public class FrameDebuggerRawEvent
{
    public int index = -1;
    public string name;
    public Shader shader;
    public string shaderName;
    public string shaderPath;
    public string shaderGuid;
    public int passIndex = -1;
    public string passName;
    public string lightMode;
    public string shaderKeywords;

    public override string ToString()
    {
        return $"#{index}: shader='{shaderName}' passIdx={passIndex} " +
               $"pass='{passName}' lightMode='{lightMode}' kw='{shaderKeywords}'";
    }
}

/// <summary>
/// A single resolved shader variant record, enriched with exact pass metadata
/// from ShaderUtil.GetShaderData.
/// </summary>
[Serializable]
public class ShaderVariantRecord
{
    // --- Shader identity ---
    public string shaderName;
    public string shaderPath;
    public string shaderGuid;

    // --- Pass identity (enriched from ShaderPassExtractor) ---
    public int passType = (int)PassType.Normal;
    public string passTypeName = PassType.Normal.ToString();
    public int passIndex = -1;
    public int subShaderIndex = -1;
    public int localPassIndex = -1;
    public string passName;
    public string lightMode;

    // --- Variant data ---
    public List<string> keywords = new();

    // --- Observational metadata (from FrameDebugger) ---
    public List<string> scenePaths = new();
    public List<string> frameEventNames = new();

    /// <summary>
    /// Build the composite dedup key: shader|passIndex|keywords|passName|lightMode.
    /// All 5 fields are needed because:
    /// - shader: different shaders have different pass layouts
    /// - passIndex: same PassType can appear in multiple passes
    /// - keywords: same pass with different keyword combos are different variants
    /// - passName: FrameDebugger may report passIndex=-1; passName disambiguates
    /// - lightMode: extra disambiguation when both passIndex and passName are unreliable
    /// </summary>
    public string DedupKey
    {
        get
        {
            var kwKey = KeywordUtils.ToKey(keywords);
            return $"{shaderName}|{passIndex}|{kwKey}|{passName}|{lightMode}";
        }
    }

    public override string ToString()
    {
        return $"shader='{shaderName}' passIdx={passIndex} " +
               $"pass='{passName}' lightMode='{lightMode}' " +
               $"type={passTypeName} kw=[{string.Join(",", keywords)}]";
    }
}

// ====================================================================
// Variant Recorder
// ====================================================================

/// <summary>
/// Merges FrameDebugger capture data with ShaderUtil pass metadata to build
/// a pass-aware shader variant collection.
///
/// Key capabilities:
/// 1. Resolve pass identity from FrameDebugger passIndex using ShaderPassExtractor
/// 2. Fallback to FindBestMatch (passName -> lightMode -> PassType) when passIndex is -1
/// 3. Composite dedup key: shader|passIndex|keywords|passName|lightMode
/// 4. Sort by (shader, passIndex, keywords)
/// 5. Export to CSV and JSON
/// </summary>
public static class VariantRecorder
{
    /// <summary>
    /// Merge a list of FrameDebugger raw events with ShaderUtil pass metadata.
    /// Resolves each event's pass identity and deduplicates by composite key.
    /// Returns records sorted by (shader, passIndex, keywords).
    /// </summary>
    /// <param name="rawEvents">List of FrameDebuggerRawEvent from FrameDebuggerReflect.StartCapture.</param>
    /// <param name="resolvePassIdentity">
    /// If true (default), resolve pass identity from the FrameDebugger passIndex
    /// using ShaderPassExtractor. If false, use raw event fields directly.
    /// </param>
    /// <returns>Sorted, deduplicated list of ShaderVariantRecord.</returns>
    public static List<ShaderVariantRecord> Merge(
        List<FrameDebuggerRawEvent> rawEvents,
        bool resolvePassIdentity = true)
    {
        var records = new List<ShaderVariantRecord>();
        var seen = new HashSet<string>();

        foreach (var raw in rawEvents)
        {
            if (raw.shader == null)
            {
                // Skip events without a valid shader (e.g., "Clear" events)
                continue;
            }

            // 1. Resolve pass identity
            ShaderPassInfo passInfo = null;
            if (resolvePassIdentity)
            {
                passInfo = raw.passIndex >= 0
                    ? ShaderPassExtractor.FindByIndex(raw.shader, raw.passIndex)
                    : ShaderPassExtractor.FindBestMatch(
                        raw.shader, PassType.Normal, raw.passName, raw.lightMode);
            }

            // 2. Normalize keywords
            var normalizedKeywords = KeywordUtils.Normalize(raw.shaderKeywords);

            // 3. Build the record
            var record = new ShaderVariantRecord
            {
                shaderName = raw.shaderName,
                shaderPath = raw.shaderPath,
                shaderGuid = raw.shaderGuid,

                // Pass identity from resolved metadata, falling back to raw data
                passIndex = passInfo?.passIndex ?? raw.passIndex,
                subShaderIndex = passInfo?.subShaderIndex ?? -1,
                localPassIndex = passInfo?.localPassIndex ?? -1,
                passName = passInfo?.passName ?? raw.passName,
                lightMode = passInfo?.lightMode ?? raw.lightMode,
                passType = passInfo?.passType ?? (int)GuessPassTypeFromRaw(raw),
                passTypeName = passInfo != null
                    ? passInfo.passTypeName
                    : ((PassType)GuessPassTypeFromRaw(raw)).ToString(),

                keywords = normalizedKeywords,
            };

            // 4. Observational metadata
            if (!string.IsNullOrEmpty(raw.name))
                record.frameEventNames.Add(raw.name);

            // 5. Deduplicate by composite key
            var key = record.DedupKey;
            if (seen.Add(key))
            {
                records.Add(record);
            }
            else
            {
                // Merge observational data into existing record
                var existing = records.Find(r => r.DedupKey == key);
                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(raw.name)
                        && !existing.frameEventNames.Contains(raw.name))
                        existing.frameEventNames.Add(raw.name);
                }
            }
        }

        // 6. Sort by (shader, passIndex, keywords)
        records.Sort(CompareRecords);

        return records;
    }

    /// <summary>
    /// Fallback PassType guessing when ShaderPassExtractor is not available.
    /// Used when resolvePassIdentity is false or extraction failed.
    /// </summary>
    static PassType GuessPassTypeFromRaw(FrameDebuggerRawEvent raw)
    {
        var value = !string.IsNullOrEmpty(raw.lightMode) ? raw.lightMode : raw.passName;
        if (string.IsNullOrEmpty(value)) return PassType.Normal;

        if (value.IndexOf("ShadowCaster", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("Caster", StringComparison.OrdinalIgnoreCase) >= 0)
            return PassType.ShadowCaster;
        if (value.IndexOf("Meta", StringComparison.OrdinalIgnoreCase) >= 0)
            return PassType.Meta;
        if (value.IndexOf("Deferred", StringComparison.OrdinalIgnoreCase) >= 0)
            return PassType.Deferred;
        if (value.IndexOf("ForwardBase", StringComparison.OrdinalIgnoreCase) >= 0)
            return PassType.ForwardBase;
        if (value.IndexOf("ForwardAdd", StringComparison.OrdinalIgnoreCase) >= 0)
            return PassType.ForwardAdd;
        if (value.IndexOf("Motion", StringComparison.OrdinalIgnoreCase) >= 0)
            return PassType.MotionVectors;

        return PassType.Normal;
    }

    /// <summary>
    /// Sort comparator: shader (ascending) -> passIndex (ascending) -> keywords (ascending).
    /// </summary>
    static int CompareRecords(ShaderVariantRecord a, ShaderVariantRecord b)
    {
        int shaderCompare = string.Compare(a.shaderName, b.shaderName, StringComparison.Ordinal);
        if (shaderCompare != 0) return shaderCompare;

        int passCompare = a.passIndex.CompareTo(b.passIndex);
        if (passCompare != 0) return passCompare;

        // Compare keyword lists element by element
        int minCount = Math.Min(a.keywords.Count, b.keywords.Count);
        for (int i = 0; i < minCount; i++)
        {
            int kwCompare = string.Compare(
                a.keywords[i], b.keywords[i], StringComparison.Ordinal);
            if (kwCompare != 0) return kwCompare;
        }
        return a.keywords.Count.CompareTo(b.keywords.Count);
    }

    // ====================================================================
    // Export
    // ====================================================================

    /// <summary>
    /// Export variant records to CSV format.
    /// </summary>
    public static string ToCsv(List<ShaderVariantRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("shaderName,shaderPath,shaderGuid,passIndex,subShaderIndex," +
                      "localPassIndex,passName,lightMode,passType,passTypeName,keywords");

        foreach (var r in records)
        {
            sb.Append(EscapeCsv(r.shaderName)).Append(',');
            sb.Append(EscapeCsv(r.shaderPath)).Append(',');
            sb.Append(EscapeCsv(r.shaderGuid)).Append(',');
            sb.Append(r.passIndex).Append(',');
            sb.Append(r.subShaderIndex).Append(',');
            sb.Append(r.localPassIndex).Append(',');
            sb.Append(EscapeCsv(r.passName)).Append(',');
            sb.Append(EscapeCsv(r.lightMode)).Append(',');
            sb.Append(r.passType).Append(',');
            sb.Append(EscapeCsv(r.passTypeName)).Append(',');
            sb.AppendLine(EscapeCsv(string.Join(" ", r.keywords)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Save variant records to a CSV file.
    /// </summary>
    public static void SaveToCsv(List<ShaderVariantRecord> records, string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        File.WriteAllText(fullPath, ToCsv(records), Encoding.UTF8);
        Debug.Log($"[VariantRecorder] Saved {records.Count} records -> {fullPath}");
    }

    /// <summary>
    /// Export variant records to JSON format (using JsonUtility).
    /// </summary>
    public static string ToJson(List<ShaderVariantRecord> records, bool prettyPrint = true)
    {
        var wrapper = new ShaderVariantCollectionJson { records = records };
        return JsonUtility.ToJson(wrapper, prettyPrint);
    }

    /// <summary>
    /// Save variant records to a JSON file.
    /// </summary>
    public static void SaveToJson(List<ShaderVariantRecord> records, string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        File.WriteAllText(fullPath, ToJson(records, true), Encoding.UTF8);
        Debug.Log($"[VariantRecorder] Saved {records.Count} records -> {fullPath}");
    }

    /// <summary>
    /// Save variant records as a ScriptableObject asset.
    /// </summary>
    public static void SaveToAsset(List<ShaderVariantRecord> records, string assetPath)
    {
        var so = ScriptableObject.CreateInstance<ShaderVariantCollectionAsset>();
        so.records = records;

        string fullPath = Path.GetFullPath(assetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

        // Convert to an asset-relative path if it's under the project
        string dataPath = Path.GetFullPath(Application.dataPath);
        if (fullPath.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
        {
            string relativePath = "Assets/" + fullPath.Substring(dataPath.Length + 1);
            AssetDatabase.CreateAsset(so, relativePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[VariantRecorder] Saved asset -> {relativePath}");
        }
        else
        {
            // Fall back to JSON if outside the project
            SaveToJson(records, assetPath + ".json");
        }
    }

    // --- CSV escaping ---

    static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    /// <summary>Clear all cached shader pass metadata.</summary>
    public static void ResetCache()
    {
        ShaderPassExtractor.Reset();
    }
}

// ====================================================================
// JSON wrapper (for serialization via JsonUtility)
// ====================================================================

[Serializable]
public class ShaderVariantCollectionJson
{
    public List<ShaderVariantRecord> records;
}

/// <summary>
/// ScriptableObject wrapper for storing variant records as an asset.
/// </summary>
public class ShaderVariantCollectionAsset : ScriptableObject
{
    public List<ShaderVariantRecord> records = new();
}
