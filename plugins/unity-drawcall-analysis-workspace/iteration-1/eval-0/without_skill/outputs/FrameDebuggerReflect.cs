using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Raw event data extracted from FrameDebuggerEventData via reflection.
/// Contains all shader, pass, keyword, and geometry information for a single
/// Frame Debugger draw-call event.
/// </summary>
public class FrameDebuggerRawEvent
{
    /// <summary>Event index within the current captured frame.</summary>
    public int index = -1;

    /// <summary>Human-readable event name (e.g. "Draw Mesh", "Clear").</summary>
    public string name;

    /// <summary>Unity Shader object (resolved from instance ID).</summary>
    public Shader shader;

    /// <summary>Original shader name assigned to the material.</summary>
    public string shaderName;

    /// <summary>Actual shader used at render time (may differ from original).</summary>
    public string realShaderName;

    /// <summary>Asset path of the shader.</summary>
    public string shaderPath;

    /// <summary>GUID of the shader asset.</summary>
    public string shaderGuid;

    /// <summary>Global pass index across all subshaders.</summary>
    public int passIndex = -1;

    /// <summary>Pass name as defined in the shader.</summary>
    public string passName;

    /// <summary>Value of the LightMode tag for this pass.</summary>
    public string lightMode;

    /// <summary>Which subshader is active.</summary>
    public int subShaderIndex = -1;

    /// <summary>Material-level shader keywords (space-separated).</summary>
    public string shaderKeywords;

    /// <summary>Renderer object (Material, Mesh, etc.) associated with the event.</summary>
    public Object eventObject;

    /// <summary>Mesh being rendered, if applicable.</summary>
    public Mesh mesh;

    /// <summary>Vertex count for the draw call.</summary>
    public int vertexCount;

    /// <summary>Index count for the draw call.</summary>
    public int indexCount;

    /// <summary>Instance count for instanced draw calls.</summary>
    public int instanceCount;

    /// <summary>Draw call count (for combined calls).</summary>
    public int drawCallCount;

    /// <summary>
    /// Nested shader info keywords. Each element contains name, flags, and scope info.
    /// This represents the compiled shader variant keywords (not material-level).
    /// </summary>
    public FrameDebuggerRawKeyword[] shaderInfoKeywords;

    /// <summary>Whether GetFrameEventData returned true for this event.</summary>
    public bool hasData;
}

/// <summary>
/// Represents a single keyword entry from the Frame Debugger's shader info.
/// </summary>
public class FrameDebuggerRawKeyword
{
    /// <summary>Keyword string (e.g. "_MAIN_LIGHT_SHADOWS").</summary>
    public string name;

    /// <summary>Keyword flags bitmask.</summary>
    public int flags;

    /// <summary>Whether this is a global (non-local) shader keyword.</summary>
    public bool isGlobal;

    /// <summary>Whether this is a dynamic keyword (runtime-computed).</summary>
    public bool isDynamic;

    public override string ToString()
    {
        return $"{name} flags={flags} global={isGlobal} dynamic={isDynamic}";
    }
}

/// <summary>
/// Pure-reflection wrapper for UnityEditorInternal.FrameDebuggerUtility and
/// FrameDebuggerEventData. Does NOT depend on InternalAPIEditorBridge.
///
/// Uses Delegate.CreateDelegate for all static method/property access (~10x faster
/// than MethodInfo.Invoke), with MethodInfo.Invoke only where the internal param
/// type (FrameDebuggerEventData) prevents compile-time delegate binding.
///
/// Usage:
///   FrameDebuggerReflect.SetEnabled(true, FrameDebuggerReflect.GetRemotePlayerGUID());
///   // ... wait for events ...
///   int count = FrameDebuggerReflect.GetCount();
///   object data = FrameDebuggerReflect.CreateEventData();
///   FrameDebuggerReflect.GetFrameEventData(index, data);
///   string shaderName = FrameDebuggerReflect.GetOriginalShaderName(data);
/// </summary>
public static class FrameDebuggerReflect
{
    // ---------------------------------------------------------------
    // Resolved internal types
    // ---------------------------------------------------------------

    private static Type s_UtilType;         // FrameDebuggerUtility
    private static Type s_EventDataType;    // FrameDebuggerEventData
    private static Type s_ShaderInfoType;   // FrameDebuggerEventData.ShaderInfo (nested)
    private static Type s_KeywordType;      // ShaderInfo.Keyword struct

    // ---------------------------------------------------------------
    // Delegate-backed fast accessors (Delegate.CreateDelegate)
    // ---------------------------------------------------------------

    // Methods
    private static Action<bool, int> s_SetEnabled;
    private static Func<int> s_GetRemotePlayerGUID;
    private static Func<Array> s_GetFrameEvents;
    private static Func<int, string> s_GetFrameEventInfoName;
    private static Func<int, Object> s_GetFrameEventObject;

    // Properties
    private static Func<int> s_GetCount;
    private static Func<int> s_GetLimit;
    private static Action<int> s_SetLimit;

    // ---------------------------------------------------------------
    // MethodInfo-backed accessors (internal param type prevents delegate)
    // ---------------------------------------------------------------

    private static MethodInfo s_MI_GetFrameEventData;

    // ---------------------------------------------------------------
    // FrameDebuggerEventData instance field accessors
    // ---------------------------------------------------------------

    private static FieldInfo s_FI_Mesh;
    private static FieldInfo s_FI_OriginalShaderName;
    private static FieldInfo s_FI_RealShaderName;
    private static FieldInfo s_FI_PassName;
    private static FieldInfo s_FI_PassLightMode;
    private static FieldInfo s_FI_ShaderInstanceID;
    private static FieldInfo s_FI_SubShaderIndex;
    private static FieldInfo s_FI_ShaderPassIndex;
    private static FieldInfo s_FI_ShaderKeywords;
    private static FieldInfo s_FI_VertexCount;
    private static FieldInfo s_FI_IndexCount;
    private static FieldInfo s_FI_InstanceCount;
    private static FieldInfo s_FI_DrawCallCount;
    private static FieldInfo s_FI_ShaderInfo;

    // ---------------------------------------------------------------
    // ShaderInfo nested field accessors
    // ---------------------------------------------------------------

    private static FieldInfo s_FI_SI_Keywords;
    private static FieldInfo s_FI_SI_Floats;
    private static FieldInfo s_FI_SI_Ints;
    private static FieldInfo s_FI_SI_Vectors;
    private static FieldInfo s_FI_SI_Matrices;
    private static FieldInfo s_FI_SI_Textures;
    private static FieldInfo s_FI_SI_Buffers;
    private static FieldInfo s_FI_SI_CBuffers;

    // ---------------------------------------------------------------
    // Keyword struct field accessors
    // ---------------------------------------------------------------

    private static FieldInfo s_FI_KW_Name;
    private static FieldInfo s_FI_KW_Flags;
    private static FieldInfo s_FI_KW_IsGlobal;
    private static FieldInfo s_FI_KW_IsDynamic;

    // ---------------------------------------------------------------
    // Public state
    // ---------------------------------------------------------------

    /// <summary>
    /// Returns true when both FrameDebuggerUtility and FrameDebuggerEventData
    /// types were successfully resolved via reflection.
    /// </summary>
    public static bool IsAvailable => s_UtilType != null && s_EventDataType != null;

    // ---------------------------------------------------------------
    // Static constructor — resolve everything once, fail early
    // ---------------------------------------------------------------

    static FrameDebuggerReflect()
    {
        try
        {
            Assembly editorAssembly = typeof(Editor).Assembly;

            s_UtilType = editorAssembly.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
            s_EventDataType = editorAssembly.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData");

            if (s_UtilType == null)
            {
                Debug.LogError("[FrameDebuggerReflect] Failed to resolve " +
                    "'UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility'.");
                return;
            }
            if (s_EventDataType == null)
            {
                Debug.LogError("[FrameDebuggerReflect] Failed to resolve " +
                    "'UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData'.");
                return;
            }

            const BindingFlags staticFlags =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            // ---- Static methods ----
            s_SetEnabled = CreateStaticDelegate<Action<bool, int>>(
                "SetEnabled", staticFlags, new[] { typeof(bool), typeof(int) });
            s_GetRemotePlayerGUID = CreateStaticDelegate<Func<int>>(
                "GetRemotePlayerGUID", staticFlags);
            s_GetFrameEvents = CreateStaticDelegate<Func<Array>>(
                "GetFrameEvents", staticFlags);
            s_GetFrameEventInfoName = CreateStaticDelegate<Func<int, string>>(
                "GetFrameEventInfoName", staticFlags);
            s_GetFrameEventObject = CreateStaticDelegate<Func<int, Object>>(
                "GetFrameEventObject", staticFlags);

            // ---- Properties ----
            s_GetCount = CreatePropertyGetter<Func<int>>("count", staticFlags);
            s_GetLimit = CreatePropertyGetter<Func<int>>("limit", staticFlags);
            s_SetLimit = CreatePropertySetter<Action<int>>("limit", staticFlags);

            // ---- GetFrameEventData (internal param type — must use MethodInfo.Invoke) ----
            s_MI_GetFrameEventData = s_UtilType.GetMethod(
                "GetFrameEventData", staticFlags, null,
                new[] { typeof(int), s_EventDataType }, null);

            // ---- Event data instance fields ----
            const BindingFlags instanceFlags =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            s_FI_Mesh = s_EventDataType.GetField("m_Mesh", instanceFlags);
            s_FI_OriginalShaderName = s_EventDataType.GetField("m_OriginalShaderName", instanceFlags);
            s_FI_RealShaderName = s_EventDataType.GetField("m_RealShaderName", instanceFlags);
            s_FI_PassName = s_EventDataType.GetField("m_PassName", instanceFlags);
            s_FI_PassLightMode = s_EventDataType.GetField("m_PassLightMode", instanceFlags);
            s_FI_ShaderInstanceID = s_EventDataType.GetField("m_ShaderInstanceID", instanceFlags);
            s_FI_SubShaderIndex = s_EventDataType.GetField("m_SubShaderIndex", instanceFlags);
            s_FI_ShaderPassIndex = s_EventDataType.GetField("m_ShaderPassIndex", instanceFlags);
            s_FI_ShaderKeywords = s_EventDataType.GetField("shaderKeywords", instanceFlags);
            s_FI_VertexCount = s_EventDataType.GetField("m_VertexCount", instanceFlags);
            s_FI_IndexCount = s_EventDataType.GetField("m_IndexCount", instanceFlags);
            s_FI_InstanceCount = s_EventDataType.GetField("m_InstanceCount", instanceFlags);
            s_FI_DrawCallCount = s_EventDataType.GetField("m_DrawCallCount", instanceFlags);
            s_FI_ShaderInfo = s_EventDataType.GetField("m_ShaderInfo", instanceFlags);

            // ---- Resolve nested ShaderInfo type and its fields ----
            if (s_FI_ShaderInfo != null)
            {
                s_ShaderInfoType = s_FI_ShaderInfo.FieldType;

                if (s_ShaderInfoType != null)
                {
                    s_FI_SI_Keywords = s_ShaderInfoType.GetField("m_Keywords", instanceFlags);
                    s_FI_SI_Floats = s_ShaderInfoType.GetField("m_Floats", instanceFlags);
                    s_FI_SI_Ints = s_ShaderInfoType.GetField("m_Ints", instanceFlags);
                    s_FI_SI_Vectors = s_ShaderInfoType.GetField("m_Vectors", instanceFlags);
                    s_FI_SI_Matrices = s_ShaderInfoType.GetField("m_Matrices", instanceFlags);
                    s_FI_SI_Textures = s_ShaderInfoType.GetField("m_Textures", instanceFlags);
                    s_FI_SI_Buffers = s_ShaderInfoType.GetField("m_Buffers", instanceFlags);
                    s_FI_SI_CBuffers = s_ShaderInfoType.GetField("m_CBuffers", instanceFlags);

                    // ---- Resolve Keyword struct type and its fields ----
                    if (s_FI_SI_Keywords != null)
                    {
                        s_KeywordType = s_FI_SI_Keywords.FieldType.GetElementType();

                        if (s_KeywordType != null)
                        {
                            s_FI_KW_Name = s_KeywordType.GetField("m_Name", instanceFlags);
                            s_FI_KW_Flags = s_KeywordType.GetField("m_Flags", instanceFlags);
                            s_FI_KW_IsGlobal = s_KeywordType.GetField("m_IsGlobal", instanceFlags);
                            s_FI_KW_IsDynamic = s_KeywordType.GetField("m_IsDynamic", instanceFlags);
                        }
                    }
                }
            }

            Debug.Log("[FrameDebuggerReflect] Reflection initialization complete.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[FrameDebuggerReflect] Static constructor failed: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ===============================================================
    // Public API — FrameDebugger lifecycle
    // ===============================================================

    /// <summary>Enable or disable the Frame Debugger for the given remote player GUID.</summary>
    /// <param name="enable">True to enable, false to disable.</param>
    /// <param name="guid">Remote player GUID (use -1 for local player).</param>
    public static void SetEnabled(bool enable, int guid)
    {
        s_SetEnabled?.Invoke(enable, guid);
    }

    /// <summary>Returns the remote player GUID (-1 for local player).</summary>
    public static int GetRemotePlayerGUID()
    {
        return s_GetRemotePlayerGUID != null ? s_GetRemotePlayerGUID() : -1;
    }

    /// <summary>
    /// Enable the Frame Debugger for the local player.
    /// Equivalent to SetEnabled(true, GetRemotePlayerGUID()).
    /// </summary>
    public static void Enable()
    {
        SetEnabled(true, GetRemotePlayerGUID());
    }

    /// <summary>
    /// Disable the Frame Debugger for the local player.
    /// Equivalent to SetEnabled(false, GetRemotePlayerGUID()).
    /// </summary>
    public static void Disable()
    {
        SetEnabled(false, GetRemotePlayerGUID());
    }

    // ===============================================================
    // Public API — Event enumeration
    // ===============================================================

    /// <summary>Total number of captured frame events in the current frame.</summary>
    public static int GetCount()
    {
        return s_GetCount != null ? s_GetCount() : 0;
    }

    /// <summary>
    /// Current limit index. The event at (limit - 1) has its data populated
    /// for GetFrameEventData. Critical: you must set this per-event.
    /// </summary>
    public static int GetLimit()
    {
        return s_GetLimit != null ? s_GetLimit() : 0;
    }

    /// <summary>
    /// Set the limit index. After setting, wait one editor frame for data to
    /// populate, then call GetFrameEventData(limit - 1, data).
    /// </summary>
    public static void SetLimit(int value)
    {
        if (s_SetLimit != null)
        {
            s_SetLimit(value);
        }
    }

    /// <summary>
    /// Returns the raw FrameDebuggerEvent[] array. This is the internal array
    /// of all events; Length gives the total event count.
    /// </summary>
    public static Array GetFrameEvents()
    {
        return s_GetFrameEvents?.Invoke();
    }

    /// <summary>
    /// Returns the display name of the event at the given index.
    /// This works WITHOUT setting limit first (unlike GetFrameEventData).
    /// </summary>
    public static string GetFrameEventInfoName(int index)
    {
        return s_GetFrameEventInfoName != null ? s_GetFrameEventInfoName(index) : string.Empty;
    }

    /// <summary>
    /// Returns the UnityEngine.Object associated with the event at the given index.
    /// Typically a Material, Mesh, or null for non-rendering events.
    /// </summary>
    public static Object GetFrameEventObject(int index)
    {
        return s_GetFrameEventObject?.Invoke(index);
    }

    // ===============================================================
    // Public API — Event data access
    // ===============================================================

    /// <summary>
    /// Populates a FrameDebuggerEventData object with data for the event at index.
    /// IMPORTANT: This only returns valid data when index == (limit - 1).
    /// You MUST call SetLimit(index + 1) and wait one editor frame first.
    /// </summary>
    /// <param name="index">Event index to query.</param>
    /// <param name="data">A FrameDebuggerEventData instance created via <see cref="CreateEventData"/>.</param>
    /// <returns>True if data was populated successfully.</returns>
    public static bool GetFrameEventData(int index, object data)
    {
        if (s_MI_GetFrameEventData == null)
        {
            return false;
        }

        if (data == null)
        {
            return false;
        }

        try
        {
            return (bool)s_MI_GetFrameEventData.Invoke(null, new[] { index, data });
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a new instance of FrameDebuggerEventData using the internal
    /// constructor (bypasses visibility via Activator.CreateInstance).
    /// </summary>
    public static object CreateEventData()
    {
        if (s_EventDataType == null)
        {
            return null;
        }

        try
        {
            return Activator.CreateInstance(s_EventDataType, nonPublic: true);
        }
        catch
        {
            return null;
        }
    }

    // ===============================================================
    // Public API — Field extractors from FrameDebuggerEventData
    // ===============================================================

    /// <summary>Original shader name assigned to the material.</summary>
    public static string GetOriginalShaderName(object data)
    {
        return s_FI_OriginalShaderName?.GetValue(data) as string ?? string.Empty;
    }

    /// <summary>Actual shader name used at render time (may differ from original).</summary>
    public static string GetRealShaderName(object data)
    {
        return s_FI_RealShaderName?.GetValue(data) as string ?? string.Empty;
    }

    /// <summary>Pass name as defined in the shader.</summary>
    public static string GetPassName(object data)
    {
        return s_FI_PassName?.GetValue(data) as string ?? string.Empty;
    }

    /// <summary>Value of the LightMode tag for the active pass.</summary>
    public static string GetPassLightMode(object data)
    {
        return s_FI_PassLightMode?.GetValue(data) as string ?? string.Empty;
    }

    /// <summary>Instance ID of the Shader object. Use EditorUtility.InstanceIDToObject to resolve.</summary>
    public static int GetShaderInstanceID(object data)
    {
        if (data == null || s_FI_ShaderInstanceID == null) return 0;
        object val = s_FI_ShaderInstanceID.GetValue(data);
        return val != null ? (int)val : 0;
    }

    /// <summary>Which subshader is active.</summary>
    public static int GetSubShaderIndex(object data)
    {
        if (data == null || s_FI_SubShaderIndex == null) return -1;
        object val = s_FI_SubShaderIndex.GetValue(data);
        return val != null ? (int)val : -1;
    }

    /// <summary>Global pass index across all subshaders (-1 if unknown).</summary>
    public static int GetShaderPassIndex(object data)
    {
        if (data == null || s_FI_ShaderPassIndex == null) return -1;
        object val = s_FI_ShaderPassIndex.GetValue(data);
        return val != null ? (int)val : -1;
    }

    /// <summary>Material-level shader keywords as a space-separated string.</summary>
    public static string GetShaderKeywords(object data)
    {
        return s_FI_ShaderKeywords?.GetValue(data) as string ?? string.Empty;
    }

    /// <summary>Mesh being rendered, if applicable.</summary>
    public static Mesh GetMesh(object data)
    {
        return s_FI_Mesh?.GetValue(data) as Mesh;
    }

    /// <summary>Vertex count for the draw call.</summary>
    public static int GetVertexCount(object data)
    {
        if (data == null || s_FI_VertexCount == null) return 0;
        object val = s_FI_VertexCount.GetValue(data);
        return val != null ? (int)val : 0;
    }

    /// <summary>Index count for the draw call.</summary>
    public static int GetIndexCount(object data)
    {
        if (data == null || s_FI_IndexCount == null) return 0;
        object val = s_FI_IndexCount.GetValue(data);
        return val != null ? (int)val : 0;
    }

    /// <summary>Instance count for instanced draw calls.</summary>
    public static int GetInstanceCount(object data)
    {
        if (data == null || s_FI_InstanceCount == null) return 0;
        object val = s_FI_InstanceCount.GetValue(data);
        return val != null ? (int)val : 0;
    }

    /// <summary>Draw call count (for combined calls).</summary>
    public static int GetDrawCallCount(object data)
    {
        if (data == null || s_FI_DrawCallCount == null) return 0;
        object val = s_FI_DrawCallCount.GetValue(data);
        return val != null ? (int)val : 0;
    }

    /// <summary>
    /// Returns the raw ShaderInfo object from the event data.
    /// Use GetShaderInfoKeywords, GetShaderInfoFloats, etc. to access its contents.
    /// </summary>
    public static object GetShaderInfo(object data)
    {
        return s_FI_ShaderInfo?.GetValue(data);
    }

    // ===============================================================
    // Public API — ShaderInfo field accessors
    // ===============================================================

    /// <summary>Keyword array from the ShaderInfo object (Array of keyword structs).</summary>
    public static Array GetShaderInfoKeywords(object shaderInfo)
    {
        return s_FI_SI_Keywords?.GetValue(shaderInfo) as Array;
    }

    /// <summary>Number of keywords in the ShaderInfo keyword array.</summary>
    public static int GetShaderInfoKeywordCount(object shaderInfo)
    {
        Array arr = s_FI_SI_Keywords?.GetValue(shaderInfo) as Array;
        return arr?.Length ?? 0;
    }

    /// <summary>Number of float properties in the ShaderInfo.</summary>
    public static int GetShaderInfoFloatCount(object shaderInfo)
    {
        Array arr = s_FI_SI_Floats?.GetValue(shaderInfo) as Array;
        return arr?.Length ?? 0;
    }

    /// <summary>Number of int properties in the ShaderInfo.</summary>
    public static int GetShaderInfoIntCount(object shaderInfo)
    {
        Array arr = s_FI_SI_Ints?.GetValue(shaderInfo) as Array;
        return arr?.Length ?? 0;
    }

    /// <summary>Number of vector properties in the ShaderInfo.</summary>
    public static int GetShaderInfoVectorCount(object shaderInfo)
    {
        Array arr = s_FI_SI_Vectors?.GetValue(shaderInfo) as Array;
        return arr?.Length ?? 0;
    }

    /// <summary>Number of matrix properties in the ShaderInfo.</summary>
    public static int GetShaderInfoMatrixCount(object shaderInfo)
    {
        Array arr = s_FI_SI_Matrices?.GetValue(shaderInfo) as Array;
        return arr?.Length ?? 0;
    }

    /// <summary>Number of texture properties in the ShaderInfo.</summary>
    public static int GetShaderInfoTextureCount(object shaderInfo)
    {
        Array arr = s_FI_SI_Textures?.GetValue(shaderInfo) as Array;
        return arr?.Length ?? 0;
    }

    /// <summary>Number of buffer properties in the ShaderInfo.</summary>
    public static int GetShaderInfoBufferCount(object shaderInfo)
    {
        Array arr = s_FI_SI_Buffers?.GetValue(shaderInfo) as Array;
        return arr?.Length ?? 0;
    }

    /// <summary>Number of constant buffer properties in the ShaderInfo.</summary>
    public static int GetShaderInfoCBufferCount(object shaderInfo)
    {
        Array arr = s_FI_SI_CBuffers?.GetValue(shaderInfo) as Array;
        return arr?.Length ?? 0;
    }

    /// <summary>
    /// Extracts all shader info keywords from the ShaderInfo object as
    /// an array of FrameDebuggerRawKeyword.
    /// </summary>
    public static FrameDebuggerRawKeyword[] ExtractShaderInfoKeywords(object shaderInfo)
    {
        Array rawKeywords = GetShaderInfoKeywords(shaderInfo);
        if (rawKeywords == null || rawKeywords.Length == 0)
        {
            return Array.Empty<FrameDebuggerRawKeyword>();
        }

        var result = new FrameDebuggerRawKeyword[rawKeywords.Length];
        for (int i = 0; i < rawKeywords.Length; i++)
        {
            object kw = rawKeywords.GetValue(i);
            if (kw == null)
            {
                result[i] = new FrameDebuggerRawKeyword
                {
                    name = string.Empty,
                    flags = 0,
                    isGlobal = false,
                    isDynamic = false,
                };
                continue;
            }

            result[i] = new FrameDebuggerRawKeyword
            {
                name = GetKeywordName(kw),
                flags = GetKeywordFlags(kw),
                isGlobal = GetKeywordIsGlobal(kw),
                isDynamic = GetKeywordIsDynamic(kw),
            };
        }

        return result;
    }

    // ===============================================================
    // Public API — Keyword struct field accessors
    // ===============================================================

    /// <summary>Get a keyword struct by index from the keyword array.</summary>
    public static object GetKeyword(Array keywords, int index)
    {
        if (keywords == null) return null;
        return index >= 0 && index < keywords.Length ? keywords.GetValue(index) : null;
    }

    /// <summary>Get the keyword name from a keyword struct.</summary>
    public static string GetKeywordName(object keyword)
    {
        return s_FI_KW_Name?.GetValue(keyword) as string ?? string.Empty;
    }

    /// <summary>Get the keyword flags from a keyword struct.</summary>
    public static int GetKeywordFlags(object keyword)
    {
        if (keyword == null || s_FI_KW_Flags == null) return 0;
        object val = s_FI_KW_Flags.GetValue(keyword);
        return val != null ? (int)val : 0;
    }

    /// <summary>Whether this keyword is a global (non-local) shader keyword.</summary>
    public static bool GetKeywordIsGlobal(object keyword)
    {
        if (keyword == null || s_FI_KW_IsGlobal == null) return false;
        object val = s_FI_KW_IsGlobal.GetValue(keyword);
        return val is bool b && b;
    }

    /// <summary>Whether this keyword is a dynamic (runtime-computed) keyword.</summary>
    public static bool GetKeywordIsDynamic(object keyword)
    {
        if (keyword == null || s_FI_KW_IsDynamic == null) return false;
        object val = s_FI_KW_IsDynamic.GetValue(keyword);
        return val is bool b && b;
    }

    // ===============================================================
    // Public API — Convenience: extract everything from one event
    // ===============================================================

    /// <summary>
    /// Captures a single event into a fully-populated <see cref="FrameDebuggerRawEvent"/>.
    /// The FrameDebugger must be enabled, the limit must be set to index + 1,
    /// and one editor frame must have elapsed since setting the limit.
    /// </summary>
    /// <param name="index">Event index to capture.</param>
    /// <returns>A populated FrameDebuggerRawEvent, or null if data is unavailable.</returns>
    public static FrameDebuggerRawEvent CaptureSingleEvent(int index)
    {
        object data = CreateEventData();
        if (data == null)
        {
            return null;
        }

        bool hasData = GetFrameEventData(index, data);
        if (!hasData)
        {
            return null;
        }

        int shaderInstanceId = GetShaderInstanceID(data);
        Shader shader = shaderInstanceId != 0
            ? EditorUtility.InstanceIDToObject(shaderInstanceId) as Shader
            : null;

        object shaderInfo = GetShaderInfo(data);

        var raw = new FrameDebuggerRawEvent
        {
            index = index,
            name = GetFrameEventInfoName(index),
            shader = shader,
            shaderName = GetOriginalShaderName(data),
            realShaderName = GetRealShaderName(data),
            passIndex = GetShaderPassIndex(data),
            passName = GetPassName(data),
            lightMode = GetPassLightMode(data),
            subShaderIndex = GetSubShaderIndex(data),
            shaderKeywords = GetShaderKeywords(data),
            eventObject = GetFrameEventObject(index),
            mesh = GetMesh(data),
            vertexCount = GetVertexCount(data),
            indexCount = GetIndexCount(data),
            instanceCount = GetInstanceCount(data),
            drawCallCount = GetDrawCallCount(data),
            shaderInfoKeywords = ExtractShaderInfoKeywords(shaderInfo),
            hasData = true,
        };

        if (raw.shader != null)
        {
            raw.shaderPath = AssetDatabase.GetAssetPath(raw.shader);
            raw.shaderGuid = AssetDatabase.AssetPathToGUID(raw.shaderPath);
        }

        return raw;
    }

    // ===============================================================
    // Public API — Async capture of all events
    // ===============================================================

    /// <summary>
    /// Start an asynchronous capture of ALL Frame Debugger events via
    /// EditorApplication.update. Each event takes one editor frame to capture.
    ///
    /// The callback receives a list of <see cref="FrameDebuggerRawEvent"/> objects
    /// for all events that have valid shader data. Non-rendering events
    /// (e.g. "Clear") are included but will have hasData=false.
    ///
    /// Usage:
    /// <code>
    /// FrameDebuggerReflect.Enable();
    /// FrameDebuggerReflect.StartCapture(events => {
    ///     foreach (var e in events) {
    ///         Debug.Log($"#{e.index}: shader={e.shaderName}, pass={e.passName}");
    ///     }
    ///     FrameDebuggerReflect.Disable();
    /// });
    /// </code>
    /// </summary>
    /// <param name="onDone">
    /// Callback invoked with the complete list of captured events.
    /// Called from the main thread during EditorApplication.update.
    /// </param>
    public static void StartCapture(Action<List<FrameDebuggerRawEvent>> onDone)
    {
        var result = new List<FrameDebuggerRawEvent>();
        int targetIndex = 0;
        int eventCount = 0;

        if (onDone == null)
        {
            Debug.LogWarning("[FrameDebuggerReflect] StartCapture: onDone callback is null. Capture aborted.");
            return;
        }

        void Tick()
        {
            // Phase 1: Wait for events to populate after enabling FrameDebugger
            if (eventCount <= 0)
            {
                Array events = GetFrameEvents();
                eventCount = events != null && events.Length > 0
                    ? events.Length
                    : GetCount();

                if (eventCount <= 0)
                {
                    // Force a repaint to trigger event population
                    InternalEditorUtility.RepaintAllViews();
                    return;
                }

                Debug.Log($"[FrameDebuggerReflect] Found {eventCount} events. Starting capture...");

                // Set limit to 1 so event at index 0 has its data populated
                SetLimit(1);
                return;
            }

            // Phase 2: Capture event at target index (which is limit - 1)
            FrameDebuggerRawEvent raw = CaptureSingleEvent(targetIndex);
            if (raw != null)
            {
                result.Add(raw);
            }
            else
            {
                // Event has no data (e.g. "Clear") — still record a placeholder
                result.Add(new FrameDebuggerRawEvent
                {
                    index = targetIndex,
                    name = GetFrameEventInfoName(targetIndex),
                    hasData = false,
                });
            }

            targetIndex++;

            // Phase 3: Done or advance to next event
            if (targetIndex >= eventCount)
            {
                EditorApplication.update -= Tick;
                onDone.Invoke(result);
                return;
            }

            SetLimit(targetIndex + 1);
        }

        EditorApplication.update += Tick;
    }

    // ===============================================================
    // Private reflection helpers
    // ===============================================================

    /// <summary>Creates a static-method delegate via Delegate.CreateDelegate (~10x faster than MethodInfo.Invoke).</summary>
    private static T CreateStaticDelegate<T>(string methodName, BindingFlags flags, Type[] paramTypes = null)
        where T : Delegate
    {
        if (s_UtilType == null)
        {
            return null;
        }

        MethodInfo method = paramTypes != null
            ? s_UtilType.GetMethod(methodName, flags, null, paramTypes, null)
            : s_UtilType.GetMethod(methodName, flags);

        if (method == null)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Method '{methodName}' not found on " +
                $"'UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility'.");
            return null;
        }

        try
        {
            return (T)(object)Delegate.CreateDelegate(typeof(T), method);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Failed to create delegate for '{methodName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Creates a property-getter delegate via Delegate.CreateDelegate.</summary>
    private static T CreatePropertyGetter<T>(string propName, BindingFlags flags)
        where T : Delegate
    {
        if (s_UtilType == null)
        {
            return null;
        }

        var prop = s_UtilType.GetProperty(propName, flags);
        if (prop == null)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Property '{propName}' not found.");
            return null;
        }

        MethodInfo getMethod = prop.GetGetMethod(nonPublic: true);
        if (getMethod == null)
        {
            return null;
        }

        try
        {
            return (T)(object)Delegate.CreateDelegate(typeof(T), getMethod);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Failed to create getter delegate for '{propName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Creates a property-setter delegate via Delegate.CreateDelegate.</summary>
    private static T CreatePropertySetter<T>(string propName, BindingFlags flags)
        where T : Delegate
    {
        if (s_UtilType == null)
        {
            return null;
        }

        var prop = s_UtilType.GetProperty(propName, flags);
        if (prop == null)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Property '{propName}' not found.");
            return null;
        }

        MethodInfo setMethod = prop.GetSetMethod(nonPublic: true);
        if (setMethod == null)
        {
            return null;
        }

        try
        {
            return (T)(object)Delegate.CreateDelegate(typeof(T), setMethod);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Failed to create setter delegate for '{propName}': {ex.Message}");
            return null;
        }
    }
}
