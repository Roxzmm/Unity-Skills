# Shader Pass Metadata Extraction

Use `ShaderUtil.GetShaderData` (Editor-only API) to extract structured pass information
from a shader. The public `ShaderUtil` API is safe to call directly — no reflection needed.

## Data Types

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[Serializable]
public class ShaderPassInfo
{
    public int passIndex = -1;       // Global index across all subshaders
    public int subShaderIndex = -1;  // Subshader containing this pass
    public int localPassIndex = -1;  // Index within its subshader
    public string passName;
    public string lightMode;
    public int passType = (int)PassType.Normal;
    public string passTypeName = PassType.Normal.ToString();
}
```

## Pass Extractor

```csharp
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

public static class ShaderPassExtractor
{
    // Optional cache: keyed by Shader instance
    static readonly Dictionary<Shader, List<ShaderPassInfo>> Cache = new();

    public static List<ShaderPassInfo> ExtractPasses(Shader shader)
    {
        if (shader == null) return new List<ShaderPassInfo>();
        if (Cache.TryGetValue(shader, out var cached)) return cached;

        var passes = new List<ShaderPassInfo>();
        try
        {
            ShaderData shaderData = ShaderUtil.GetShaderData(shader);
            int passIndex = 0;

            for (int sub = 0; sub < shaderData.SubshaderCount; sub++)
            {
                var subShader = shaderData.GetSubshader(sub);
                for (int local = 0; local < subShader.PassCount; local++)
                {
                    var pass = subShader.GetPass(local);
                    var lightMode = GetLightModeTag(pass);

                    var info = new ShaderPassInfo
                    {
                        passIndex = passIndex,
                        subShaderIndex = sub,
                        localPassIndex = local,
                        passName = pass.Name,
                        lightMode = lightMode,
                        passType = (int)GuessPassType(pass.Name, lightMode),
                    };
                    info.passTypeName = ((PassType)info.passType).ToString();
                    passes.Add(info);
                    passIndex++;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ShaderPass] Extract failed for {shader.name}: {e.Message}");
        }

        Cache[shader] = passes;
        return passes;
    }

    /// <summary>Find pass by global passIndex.</summary>
    public static ShaderPassInfo FindByIndex(Shader shader, int passIndex)
    {
        var passes = ExtractPasses(shader);
        for (int i = 0; i < passes.Count; i++)
            if (passes[i].passIndex == passIndex) return passes[i];
        return null;
    }

    /// <summary>Best-effort match by passName, then lightMode, then PassType.</summary>
    public static ShaderPassInfo FindBestMatch(
        Shader shader, PassType passType, string passName, string lightMode)
    {
        var passes = ExtractPasses(shader);

        // 1. Match by exact pass name
        if (!string.IsNullOrEmpty(passName))
            for (int i = 0; i < passes.Count; i++)
                if (string.Equals(passes[i].passName, passName,
                    StringComparison.Ordinal))
                    return passes[i];

        // 2. Match by light mode
        if (!string.IsNullOrEmpty(lightMode))
            for (int i = 0; i < passes.Count; i++)
                if (string.Equals(passes[i].lightMode, lightMode,
                    StringComparison.Ordinal))
                    return passes[i];

        // 3. Fall back to any pass with this PassType
        for (int i = 0; i < passes.Count; i++)
            if ((PassType)passes[i].passType == passType)
                return passes[i];

        return null;
    }

    // --- Helpers ---

    static string GetLightModeTag(object pass)
    {
        if (pass == null) return null;
        try
        {
            var method = pass.GetType().GetMethod("FindTagValue");
            if (method == null) return null;
            var result = method.Invoke(pass,
                new object[] { new ShaderTagId("LightMode") });
            return result is ShaderTagId tag ? tag.name : result?.ToString();
        }
        catch { return null; }
    }

    static PassType GuessPassType(string passName, string lightMode)
    {
        var value = !string.IsNullOrEmpty(lightMode) ? lightMode : passName;
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

    public static void Reset() => Cache.Clear();
}
```

## Global Pass Index vs Local Pass Index

```
Shader "Example"
{
    SubShader "A" (subShaderIndex=0)
        Pass "Base"    → passIndex=0, localPassIndex=0
        Pass "Add"     → passIndex=1, localPassIndex=1
    SubShader "B" (subShaderIndex=1)
        Pass "Shadow"  → passIndex=2, localPassIndex=0
        Pass "Depth"   → passIndex=3, localPassIndex=1
}
```

The FrameDebuggerEventData `m_ShaderPassIndex` stores the **global** passIndex.
This matches `passIndex` in `ShaderPassInfo` above.

## Usage with FrameDebugger Raw Events

When you capture a draw-call event from FrameDebugger, you get a `passIndex` (global).
Use `FindByIndex(shader, passIndex)` to get the full pass metadata including
subShaderIndex, passName, lightMode, and PassType.

If the passIndex is -1 (unknown), use `FindBestMatch` with the available passName/lightMode hints.
