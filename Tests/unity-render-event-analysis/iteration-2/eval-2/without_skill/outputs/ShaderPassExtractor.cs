#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace UT.Graphics.ShaderVariantTools
{
    /// <summary>
    /// Structured metadata for a single pass within a shader, extracted from
    /// ShaderUtil.GetShaderData.  Each pass carries:
    ///   - sub-shader index (which sub-shader it belongs to)
    ///   - local pass index (position within its sub-shader)
    ///   - global pass index (flattened across all sub-shaders)
    ///   - pass Name tag
    ///   - LightMode tag (parsed from the shader source)
    ///   - PassType enum (parsed from shader metadata or guessed by name/tag)
    /// </summary>
    [Serializable]
    public struct ShaderPassInfo : IEquatable<ShaderPassInfo>
    {
        /// <summary>The shader this pass belongs to.</summary>
        public Shader Shader;
        /// <summary>0-based sub-shader index.</summary>
        public int SubShaderIndex;
        /// <summary>0-based pass index within its sub-shader.</summary>
        public int LocalPassIndex;
        /// <summary>Flattened pass index across all sub-shaders of this shader.</summary>
        public int GlobalPassIndex;
        /// <summary>Value of the Name tag in the shader (may be empty).</summary>
        public string PassName;
        /// <summary>Value of the LightMode tag in the shader (may be empty).</summary>
        public string LightMode;
        /// <summary>The PassType resolved from shader metadata (or guessed).</summary>
        public PassType PassType;

        public bool Equals(ShaderPassInfo other) =>
            Shader == other.Shader &&
            SubShaderIndex == other.SubShaderIndex &&
            LocalPassIndex == other.LocalPassIndex &&
            GlobalPassIndex == other.GlobalPassIndex &&
            PassName == other.PassName &&
            LightMode == other.LightMode &&
            PassType == other.PassType;

        public override bool Equals(object obj) =>
            obj is ShaderPassInfo other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + (Shader != null ? Shader.GetHashCode() : 0);
                hash = hash * 23 + SubShaderIndex;
                hash = hash * 23 + LocalPassIndex;
                hash = hash * 23 + GlobalPassIndex;
                hash = hash * 23 + (PassName?.GetHashCode() ?? 0);
                hash = hash * 23 + (LightMode?.GetHashCode() ?? 0);
                hash = hash * 23 + (int)PassType;
                return hash;
            }
        }

        public override string ToString() =>
            $"[{Shader?.name}] Sub#{SubShaderIndex} Pass#{LocalPassIndex} (Global#{GlobalPassIndex}) " +
            $"\"{PassName}\" LightMode={LightMode} Type={PassType}";
    }

    /// <summary>
    /// Extracts per-pass metadata from a Unity Shader using ShaderUtil.GetShaderData.
    ///
    /// In Unity 2022.3+ the API surfaces ShaderData / SubShaderData / PassData
    /// with direct properties.  For older Unity versions (or when the direct
    /// property is missing) the extractor falls back to reflection-based
    /// tag look-up via FindTagValue.
    ///
    /// API docs (Unity 2022.3):
    ///   ShaderData    — https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ShaderData.html
    ///   SubShaderData — https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ShaderData.SubShaderData.html
    ///   PassData      — https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ShaderData.PassData.html
    /// </summary>
    public static class ShaderPassExtractor
    {
        // -----------------------------------------------------------------
        //  Public API
        // -----------------------------------------------------------------

        /// <summary>
        /// Extract pass metadata for every sub-shader pass in the given shader.
        /// Returns an empty list if the shader is null, is not readable, or the API fails.
        /// </summary>
        public static List<ShaderPassInfo> ExtractAllPasses(Shader shader)
        {
            if (shader == null)
            {
                Debug.LogWarning("[ShaderPassExtractor] Input shader is null.");
                return new List<ShaderPassInfo>();
            }

            try
            {
                var shaderData = ShaderUtil.GetShaderData(shader);
                if (shaderData == null)
                {
                    Debug.LogWarning($"[ShaderPassExtractor] ShaderUtil.GetShaderData returned null for '{shader.name}'.");
                    return new List<ShaderPassInfo>();
                }

                var passes = new List<ShaderPassInfo>();
                int globalPassIndex = 0;

                for (int sub = 0; sub < shaderData.SubshaderCount; sub++)
                {
                    var subShader = shaderData.GetSubshader(sub);
                    if (subShader == null)
                        continue;

                    for (int local = 0; local < subShader.PassCount; local++)
                    {
                        var pass = subShader.GetPass(local);
                        if (pass == null)
                        {
                            globalPassIndex++;
                            continue;
                        }

                        string passName = GetPassName(pass);
                        string lightMode = GetLightMode(pass);
                        PassType passType = GetPassType(pass, passName, lightMode);

                        passes.Add(new ShaderPassInfo
                        {
                            Shader = shader,
                            SubShaderIndex = sub,
                            LocalPassIndex = local,
                            GlobalPassIndex = globalPassIndex,
                            PassName = passName ?? string.Empty,
                            LightMode = lightMode ?? string.Empty,
                            PassType = passType,
                        });

                        globalPassIndex++;
                    }
                }

                return passes;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ShaderPassExtractor] Failed to extract passes for shader '{shader.name}': {ex.Message}");
                return new List<ShaderPassInfo>();
            }
        }

        /// <summary>
        /// Batch-extract passes from multiple shaders.  Shaders that fail are silently skipped.
        /// </summary>
        public static Dictionary<Shader, List<ShaderPassInfo>> ExtractAllPassesBatch(IEnumerable<Shader> shaders)
        {
            var result = new Dictionary<Shader, List<ShaderPassInfo>>();
            if (shaders == null)
                return result;

            foreach (var shader in shaders)
            {
                if (shader == null || result.ContainsKey(shader))
                    continue;
                result[shader] = ExtractAllPasses(shader);
            }

            return result;
        }

        /// <summary>
        /// Find pass metadata by its global (flattened) pass index.
        /// Returns null if no pass with that index exists.
        /// </summary>
        public static ShaderPassInfo? FindByIndex(Shader shader, int globalPassIndex)
        {
            var passes = ExtractAllPasses(shader);
            for (int i = 0; i < passes.Count; i++)
            {
                if (passes[i].GlobalPassIndex == globalPassIndex)
                    return passes[i];
            }
            return null;
        }

        /// <summary>
        /// Best-effort match: identify which pass a FrameDebugger draw-call
        /// belongs to when the exact pass index is unknown or ambiguous.
        ///
        /// Priority chain:
        ///   1. Exact match by pass Name tag
        ///   2. Exact match by LightMode tag
        ///   3. Any pass with the given PassType
        ///   4. First pass in the sub-shader that has a non-empty Name
        ///
        /// Returns null if no pass can be matched.
        /// </summary>
        public static ShaderPassInfo? FindBestMatch(
            Shader shader, PassType passType, string passName, string lightMode)
        {
            var passes = ExtractAllPasses(shader);
            if (passes.Count == 0)
                return null;

            // 1. Exact match by pass Name
            if (!string.IsNullOrEmpty(passName))
            {
                for (int i = 0; i < passes.Count; i++)
                {
                    if (string.Equals(passes[i].PassName, passName, StringComparison.Ordinal))
                        return passes[i];
                }
            }

            // 2. Exact match by LightMode
            if (!string.IsNullOrEmpty(lightMode))
            {
                for (int i = 0; i < passes.Count; i++)
                {
                    if (string.Equals(passes[i].LightMode, lightMode, StringComparison.Ordinal))
                        return passes[i];
                }
            }

            // 3. Any pass with this PassType
            for (int i = 0; i < passes.Count; i++)
            {
                if (passes[i].PassType == passType)
                    return passes[i];
            }

            // 4. First pass with a non-empty Name (last resort)
            for (int i = 0; i < passes.Count; i++)
            {
                if (!string.IsNullOrEmpty(passes[i].PassName))
                    return passes[i];
            }

            return passes[0];
        }

        /// <summary>
        /// Given a FrameDebugger draw-call and the shader it uses, resolve the
        /// pass metadata.  This is a convenience wrapper that tries:
        ///   - FindByIndex (using the FD-reported global pass index)
        ///   - FindBestMatch (fallback when passIndex is -1 or no match found)
        /// </summary>
        public static ShaderPassInfo? ResolveFromDrawCall(
            Shader shader, int passIndex, string passName, string lightMode)
        {
            // Try exact index match first.
            if (passIndex >= 0)
            {
                var byIndex = FindByIndex(shader, passIndex);
                if (byIndex.HasValue)
                    return byIndex.Value;
            }

            // Fallback: match by name / light mode / pass type.
            return FindBestMatch(shader, PassType.Normal, passName, lightMode);
        }

        // -----------------------------------------------------------------
        //  Internal helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Get the Name tag of a pass.  Uses the direct .Name property on
        /// PassData (Unity 2022.3+) or falls back to reflection.
        /// </summary>
        private static string GetPassName(object pass)
        {
            if (pass == null)
                return string.Empty;

            try
            {
                var prop = pass.GetType().GetProperty("Name",
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                    return prop.GetValue(pass) as string ?? string.Empty;
            }
            catch
            {
                // Fall through
            }

            return string.Empty;
        }

        /// <summary>
        /// Extract the LightMode tag from a pass.
        ///
        /// Strategy:
        ///   1. Try the direct .LightMode property (Unity 2022.3+ PassData API).
        ///   2. Fall back to FindTagValue("LightMode") via reflection
        ///      (compatible with ShaderData.PassData from older Unity versions).
        ///   3. If both fail, return empty string.
        /// </summary>
        private static string GetLightMode(object pass)
        {
            if (pass == null)
                return string.Empty;

            // Strategy 1: Direct .LightMode property (Unity 2022.3+).
            try
            {
                var prop = pass.GetType().GetProperty("LightMode",
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(pass);
                    if (val is string str && !string.IsNullOrEmpty(str))
                        return str;
                }
            }
            catch
            {
                // Fall through
            }

            // Strategy 2: FindTagValue("LightMode") via reflection.
            try
            {
                var method = pass.GetType().GetMethod("FindTagValue",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(ShaderTagId) }, null);
                if (method != null)
                {
                    var result = method.Invoke(pass, new object[] { new ShaderTagId("LightMode") });
                    if (result is ShaderTagId tag && !string.IsNullOrEmpty(tag.name))
                        return tag.name;
                    return result?.ToString() ?? string.Empty;
                }
            }
            catch
            {
                // Ignore
            }

            return string.Empty;
        }

        /// <summary>
        /// Resolve the PassType enum for a pass.
        ///
        /// Strategy:
        ///   1. Try the direct .PassType / .passType property (Unity 2022.3+).
        ///   2. Fall back to guessing via passName and lightMode.
        /// </summary>
        private static PassType GetPassType(object pass, string passName, string lightMode)
        {
            if (pass == null)
                return PassType.Normal;

            // Strategy 1: Direct PassType property (Unity 2022.3+).
            try
            {
                var prop = pass.GetType().GetProperty("PassType",
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop == null)
                    prop = pass.GetType().GetProperty("passType",
                        BindingFlags.Public | BindingFlags.Instance);

                if (prop != null)
                {
                    var val = prop.GetValue(pass);
                    if (val is PassType pt)
                        return pt;
                    if (val != null)
                    {
                        // Try enum parse from integer or string.
                        if (val is int intVal && Enum.IsDefined(typeof(PassType), intVal))
                            return (PassType)intVal;
                        if (Enum.TryParse<PassType>(val.ToString(), out var parsed))
                            return parsed;
                    }
                }
            }
            catch
            {
                // Fall through
            }

            // Strategy 2: Guess from pass name and light mode.
            return GuessPassType(passName, lightMode);
        }

        /// <summary>
        /// Guess the PassType from the pass Name tag and LightMode tag.
        /// This is used as a fallback when the PassData API does not expose
        /// the PassType directly (e.g. reflection access into the raw data).
        /// </summary>
        public static PassType GuessPassType(string passName, string lightMode)
        {
            var value = !string.IsNullOrEmpty(lightMode) ? lightMode : passName;
            if (string.IsNullOrEmpty(value))
                return PassType.Normal;

            if (value.IndexOf("ShadowCaster", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Caster", StringComparison.OrdinalIgnoreCase) >= 0)
                return PassType.ShadowCaster;

            if (value.IndexOf("Meta", StringComparison.OrdinalIgnoreCase) >= 0)
                return PassType.Meta;

            if (value.IndexOf("Deferred", StringComparison.OrdinalIgnoreCase) >= 0)
                return PassType.Deferred;

            if (value.IndexOf("ForwardBase", StringComparison.OrdinalIgnoreCase) >= 0)
                return PassType.ForwardBase;

            if (value.IndexOf("ForwardAdd", StringComparison.OrdinalIgnoreCase) >= 0)
                return PassType.ForwardAdd;

            if (value.IndexOf("MotionVectors", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Motion", StringComparison.OrdinalIgnoreCase) >= 0)
                return PassType.MotionVectors;

            if (value.IndexOf("DepthOnly", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Depth", StringComparison.OrdinalIgnoreCase) >= 0)
                return PassType.Normal; // DepthOnly is a sub-type of Normal passes in URP

            if (value.IndexOf("ScenePicking", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("SceneSelection", StringComparison.OrdinalIgnoreCase) >= 0)
                return PassType.ScenePicking;

            if (value.IndexOf("Scriptable", StringComparison.OrdinalIgnoreCase) >= 0)
                return PassType.ScriptableRenderPipeline;

            return PassType.Normal;
        }
    }
}
#endif // UNITY_EDITOR
