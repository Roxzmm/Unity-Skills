#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace UT.Graphics.ShaderVariantTools
{
    /// <summary>
    /// Pass metadata extracted from ShaderUtil.GetShaderData.
    /// Carries subShader index, local/global pass indices, pass name, light mode tag, and PassType.
    /// </summary>
    public struct ShaderPassInfo : IEquatable<ShaderPassInfo>
    {
        /// <summary> The shader this pass belongs to. </summary>
        public Shader Shader;
        /// <summary> Which sub-shader this pass lives in (0-based). </summary>
        public int SubShaderIndex;
        /// <summary> Pass index within its sub-shader (0-based). </summary>
        public int LocalPassIndex;
        /// <summary> Flattened pass index across all sub-shaders of the same shader. </summary>
        public int GlobalPassIndex;
        /// <summary> The Name tag of the pass (may be empty). </summary>
        public string PassName;
        /// <summary> The LightMode tag of the pass (may be empty). </summary>
        public string LightMode;
        /// <summary> The PassType enum of the pass. </summary>
        public PassType PassType;

        public override bool Equals(object obj) =>
            obj is ShaderPassInfo other && Equals(other);

        public bool Equals(ShaderPassInfo other) =>
            Shader == other.Shader &&
            SubShaderIndex == other.SubShaderIndex &&
            LocalPassIndex == other.LocalPassIndex &&
            GlobalPassIndex == other.GlobalPassIndex &&
            PassName == other.PassName &&
            LightMode == other.LightMode &&
            PassType == other.PassType;

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
    /// In Unity 2022.3+, this provides sub-shader index, local/global pass index,
    /// pass name, LightMode tag, and PassType for every pass in every sub-shader.
    ///
    /// API reference (Unity 2022.3):
    ///   ShaderData     — https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ShaderData.html
    ///   SubShaderData  — https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ShaderData.SubShaderData.html
    ///   PassData       — https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ShaderData.PassData.html
    /// </summary>
    public static class ShaderPassExtractor
    {
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

                for (int sub = 0; sub < shaderData.SubShaderCount; sub++)
                {
                    var subData = shaderData.GetSubShaderData(sub);
                    if (subData == null)
                        continue;

                    for (int pass = 0; pass < subData.PassCount; pass++)
                    {
                        var passData = subData.GetPassData(pass);
                        if (passData == null)
                        {
                            globalPassIndex++;
                            continue;
                        }

                        passes.Add(new ShaderPassInfo
                        {
                            Shader = shader,
                            SubShaderIndex = sub,
                            LocalPassIndex = pass,
                            GlobalPassIndex = globalPassIndex,
                            PassName = passData.Name ?? string.Empty,
                            LightMode = passData.LightMode ?? string.Empty,
                            PassType = passData.PassType,
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
        /// Batch-extract passes from multiple shaders. Shaders that fail are silently skipped.
        /// </summary>
        public static Dictionary<Shader, List<ShaderPassInfo>> ExtractAllPassesBatch(IEnumerable<Shader> shaders)
        {
            var result = new Dictionary<Shader, List<ShaderPassInfo>>();
            if (shaders == null)
                return result;

            foreach (var shader in shaders)
            {
                if (shader == null)
                    continue;
                if (result.ContainsKey(shader))
                    continue;
                result[shader] = ExtractAllPasses(shader);
            }

            return result;
        }
    }
}
#endif // UNITY_EDITOR
