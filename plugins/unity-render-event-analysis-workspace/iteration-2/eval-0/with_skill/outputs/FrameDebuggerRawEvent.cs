using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Raw event data extracted from FrameDebuggerEventData via reflection.
/// No dependency on InternalAPIEditorBridge.
/// </summary>
public class FrameDebuggerRawEvent
{
    /// <summary>Index of this event in the frame.</summary>
    public int index = -1;

    /// <summary>Display name of the event (e.g., "Draw Mesh", "Clear").</summary>
    public string name;

    /// <summary>The Shader object (resolved from instance ID).</summary>
    public Shader shader;

    /// <summary>Original shader name assigned to the material.</summary>
    public string shaderName;

    /// <summary>Asset path of the shader.</summary>
    public string shaderPath;

    /// <summary>GUID of the shader asset.</summary>
    public string shaderGuid;

    /// <summary>Global pass index across all subshaders. -1 if unknown.</summary>
    public int passIndex = -1;

    /// <summary>Pass name from the shader.</summary>
    public string passName;

    /// <summary>LightMode tag value.</summary>
    public string lightMode;

    /// <summary>Material keywords as a space-separated string.</summary>
    public string shaderKeywords;

    /// <summary>Parsed list of individual keywords.</summary>
    public List<string> KeywordsList
    {
        get
        {
            if (string.IsNullOrWhiteSpace(shaderKeywords))
                return new List<string>();
            var list = new List<string>();
            foreach (var kw in shaderKeywords.Split(
                new[] { ' ', '\t', '\r', '\n' },
                System.StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(kw))
                    list.Add(kw.Trim());
            }
            return list;
        }
    }

    public override string ToString()
    {
        return $"#{index}: {name} | shader={shaderName} | pass={passName}(idx={passIndex}) | " +
               $"lightMode={lightMode} | keywords=[{shaderKeywords}]";
    }
}
