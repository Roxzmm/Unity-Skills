#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine.Rendering;

namespace UTDrawcallAnalysis
{
    /// <summary>
    /// Metadata for a single shader pass, extracted via ShaderUtil.GetShaderData.
    /// </summary>
    [Serializable]
    public struct ShaderPassInfo
    {
        /// <summary>Index of the subShader that contains this pass.</summary>
        public int subShaderIndex;

        /// <summary>Index of this pass within its subShader.</summary>
        public int localPassIndex;

        /// <summary>
        /// Flattened pass index across all subShaders.
        /// Useful when FrameDebugger reports a global (compiled) pass index.
        /// </summary>
        public int globalPassIndex;

        /// <summary>Name of the pass as declared in the shader source.</summary>
        public string passName;

        /// <summary>Value of the "LightMode" tag on this pass (may be empty for built-in passes).</summary>
        public string lightMode;

        /// <summary>The PassType classification of this pass (Normal, ForwardBase, ScriptableRenderPipeline, etc.).</summary>
        public PassType passType;
    }

    /// <summary>
    /// Extracts per-pass metadata from Unity Shader assets via ShaderUtil.GetShaderData.
    /// <para>
    /// Output includes subShaderIndex, localPassIndex, globalPassIndex, passName,
    /// LightMode tag, and PassType for every pass in every subShader.
    /// </para>
    /// <para>Must be placed in an Editor folder. Requires Unity 2018.1+ (ShaderUtil.GetShaderData).</para>
    /// </summary>
    public static class ShaderPassExtractor
    {
        /// <summary>
        /// Extract pass metadata for every pass across every subShader of the given shader.
        /// </summary>
        /// <param name="shader">The Shader asset to analyse (null-safe).</param>
        /// <returns>
        /// A list of ShaderPassInfo records, one per pass. Returns an empty list if
        /// the shader is null, has no subShaders, or if the API returns null.
        /// </returns>
        public static List<ShaderPassInfo> ExtractPasses(Shader shader)
        {
            var passes = new List<ShaderPassInfo>();
            if (shader == null)
            {
                Debug.LogWarning("[ShaderPassExtractor] Null shader provided.");
                return passes;
            }

            ShaderData shaderData;
            try
            {
                shaderData = ShaderUtil.GetShaderData(shader);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ShaderPassExtractor] ShaderUtil.GetShaderData failed for '{shader.name}': {ex.Message}");
                return passes;
            }

            if (shaderData == null)
            {
                Debug.LogWarning($"[ShaderPassExtractor] ShaderUtil.GetShaderData returned null for '{shader.name}'.");
                return passes;
            }

            int globalIndex = 0;
            int subShaderCount = shaderData.subShaderCount;

            for (int s = 0; s < subShaderCount; s++)
            {
                SubShaderData subShader;
                try
                {
                    subShader = shaderData.GetSubShader(s);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ShaderPassExtractor] Failed to get subShader {s} for '{shader.name}': {ex.Message}");
                    continue;
                }

                if (subShader == null) continue;

                int passCount = subShader.passCount;

                for (int p = 0; p < passCount; p++)
                {
                    PassData pass;
                    try
                    {
                        pass = subShader.GetPass(p);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ShaderPassExtractor] Failed to get pass {p} in subShader {s} for '{shader.name}': {ex.Message}");
                        continue;
                    }

                    if (pass == null) continue;

                    // Prefer the dedicated passIndex property; fall back to the loop index.
                    int localPassIndex = p;
                    try { localPassIndex = pass.passIndex; }
                    catch { /* property unavailable on this Unity version */ }

                    string passName = string.Empty;
                    try { passName = pass.name ?? string.Empty; } catch { }

                    string lightMode = string.Empty;
                    try { lightMode = pass.GetTag("LightMode", false) ?? string.Empty; } catch { }

                    PassType passType = PassType.Normal;
                    try { passType = pass.type; } catch { }

                    passes.Add(new ShaderPassInfo
                    {
                        subShaderIndex = s,
                        localPassIndex = localPassIndex,
                        globalPassIndex = globalIndex,
                        passName = passName,
                        lightMode = lightMode,
                        passType = passType,
                    });

                    globalIndex++;
                }
            }

            return passes;
        }

        /// <summary>
        /// Non-throwing variant. Returns true if at least one pass was extracted.
        /// </summary>
        public static bool TryExtractPasses(Shader shader, out List<ShaderPassInfo> passes)
        {
            passes = ExtractPasses(shader);
            return passes.Count > 0;
        }

        /// <summary>
        /// Build a dictionary keyed by globalPassIndex (flattened across all subShaders).
        /// </summary>
        public static Dictionary<int, ShaderPassInfo> BuildGlobalPassLookup(Shader shader)
        {
            var passes = ExtractPasses(shader);
            var lookup = new Dictionary<int, ShaderPassInfo>(passes.Count);
            foreach (var pass in passes)
            {
                if (!lookup.ContainsKey(pass.globalPassIndex))
                    lookup[pass.globalPassIndex] = pass;
            }
            return lookup;
        }

        /// <summary>
        /// Build a dictionary keyed by localPassIndex.
        /// When multiple subShaders share the same local pass index,
        /// only the first one encountered is kept.
        /// </summary>
        public static Dictionary<int, ShaderPassInfo> BuildLocalPassLookup(Shader shader)
        {
            var passes = ExtractPasses(shader);
            var lookup = new Dictionary<int, ShaderPassInfo>(passes.Count);
            foreach (var pass in passes)
            {
                if (!lookup.ContainsKey(pass.localPassIndex))
                    lookup[pass.localPassIndex] = pass;
            }
            return lookup;
        }
    }
}

#endif // UNITY_EDITOR
