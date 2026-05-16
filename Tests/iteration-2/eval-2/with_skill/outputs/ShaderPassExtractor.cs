using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Extracts structured pass metadata from a Shader using ShaderUtil.GetShaderData (public Editor API).
/// Provides PassType guessing from passName/lightMode, exact pass lookups by global passIndex,
/// and a FindBestMatch fallback chain: passName -> lightMode -> PassType.
/// </summary>
public static class ShaderPassExtractor
{
    /// <summary>Optional cache: keyed by Shader instance.</summary>
    static readonly Dictionary<Shader, List<ShaderPassInfo>> Cache = new();

    /// <summary>
    /// Extract all passes from the given shader. Results are cached per Shader instance.
    /// </summary>
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
            Debug.LogWarning($"[ShaderPassExtractor] Extract failed for {shader.name}: {e.Message}");
        }

        Cache[shader] = passes;
        return passes;
    }

    /// <summary>
    /// Find a pass by its global passIndex (exact match).
    /// </summary>
    public static ShaderPassInfo FindByIndex(Shader shader, int passIndex)
    {
        var passes = ExtractPasses(shader);
        for (int i = 0; i < passes.Count; i++)
            if (passes[i].passIndex == passIndex) return passes[i];
        return null;
    }

    /// <summary>
    /// Best-effort fallback matching when passIndex is unavailable (-1).
    /// Priority chain: passName -> lightMode -> PassType.
    /// </summary>
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

    /// <summary>
    /// Extract the LightMode tag value from a shader pass via reflection
    /// (pass.FindTagValue is internal).
    /// </summary>
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

    /// <summary>
    /// Guess PassType from passName or lightMode string content.
    /// This is critical for correctly categorizing ShadowCaster/Meta/Deferred/Forward variants
    /// when passIndex is unavailable from FrameDebugger.
    /// </summary>
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

    /// <summary>Clear the pass metadata cache.</summary>
    public static void Reset() => Cache.Clear();
}

/// <summary>
/// Serializable data for a single shader pass with its metadata.
/// </summary>
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

    public override string ToString()
    {
        return $"[{passIndex}] sub={subShaderIndex} local={localPassIndex} " +
               $"name='{passName}' lightMode='{lightMode}' type={passTypeName}";
    }
}
