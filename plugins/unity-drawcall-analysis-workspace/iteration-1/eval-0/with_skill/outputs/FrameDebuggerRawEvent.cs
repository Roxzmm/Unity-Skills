using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Raw event data extracted from FrameDebuggerEventData via reflection.
/// All fields are populated by FrameDebuggerReflect during event capture.
/// </summary>
public class FrameDebuggerRawEvent
{
    // --- Event identity ---
    public int index = -1;
    public string name;

    // --- Shader info ---
    public Shader shader;
    public string shaderName;           // m_OriginalShaderName
    public string realShaderName;       // m_RealShaderName
    public string shaderPath;
    public string shaderGuid;

    // --- Pass info ---
    public int passIndex = -1;          // m_ShaderPassIndex (global pass index)
    public int subShaderIndex = -1;     // m_SubShaderIndex
    public string passName;             // m_PassName
    public string lightMode;            // m_PassLightMode

    // --- Keywords ---
    public string shaderKeywords;       // shaderKeywords (space-separated material keywords)

    /// <summary>
    /// Detailed keyword entries extracted from m_ShaderInfo.m_Keywords[].
    /// Includes per-keyword flags, global/dynamic state.
    /// </summary>
    public List<FrameDebuggerRawKeyword> rawKeywords;

    // --- Rendering stats ---
    public int vertexCount = -1;
    public int indexCount = -1;
    public int instanceCount = -1;
    public int drawCallCount = -1;
    public Mesh mesh;

    /// <summary>
    /// True if GetFrameEventData returned true for this event.
    /// Non-rendering events (e.g. "Clear") return false and will have default field values.
    /// </summary>
    public bool hasData;

    // --- Convenience ---

    /// <summary>Parsed, deduplicated, sorted list of keywords from shaderKeywords string.</summary>
    public List<string> ParsedKeywords
    {
        get
        {
            if (string.IsNullOrWhiteSpace(shaderKeywords))
                return new List<string>(0);
            var set = new HashSet<string>(StringComparer.Ordinal);
            var parts = shaderKeywords.Split(
                new[] { ' ', '\t', '\r', '\n', ';', ',' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) set.Add(trimmed);
            }
            var result = new List<string>(set);
            result.Sort(StringComparer.Ordinal);
            return result;
        }
    }

    public override string ToString()
    {
        return $"#{index} [{passIndex}] {shaderName} / {passName}  kw=\"{shaderKeywords}\"  hasData={hasData}";
    }
}

/// <summary>
/// Detailed keyword entry from m_ShaderInfo.m_Keywords[].
/// </summary>
public class FrameDebuggerRawKeyword
{
    public string name;
    public int flags;
    public bool isGlobal;
    public bool isDynamic;

    public override string ToString()
    {
        return $"{name} (global={isGlobal}, dynamic={isDynamic}, flags={flags})";
    }
}
