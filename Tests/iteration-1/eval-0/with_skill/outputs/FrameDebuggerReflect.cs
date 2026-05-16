using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// Reflection-based wrapper for Unity's internal FrameDebuggerUtility API.
///
/// Uses Delegate.CreateDelegate for static methods and property accessors (~10x faster
/// than MethodInfo.Invoke). GetFrameEventData uses MethodInfo.Invoke because its parameter
/// type (FrameDebuggerEventData) is an internal type only resolvable at runtime.
///
/// Usage:
///   FrameDebuggerReflect.SetEnabled(true, FrameDebuggerReflect.GetRemotePlayerGUID());
///   // ... wait for events ...
///   var data = FrameDebuggerReflect.CreateEventData();
///   if (FrameDebuggerReflect.GetFrameEventData(index, data)) { ... }
///
/// Or use the async capture loop:
///   FrameDebuggerReflect.StartCapture(events => { ... });
///
/// IMPORTANT: Requires an active Editor GameView. Does not work in -batchmode.
/// </summary>
public static class FrameDebuggerReflect
{
    // ========================================================================
    // Resolved types
    // ========================================================================

    /// <summary>UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility</summary>
    private static Type _tUtil;

    /// <summary>UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData</summary>
    private static Type _tEventData;

    /// <summary>UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEvent (struct)</summary>
    private static Type _tEvent;

    /// <summary>FrameDebuggerEventData.m_ShaderInfo nested type</summary>
    private static Type _tShaderInfo;

    /// <summary>ShaderInfo.m_Keywords[] element struct</summary>
    private static Type _tKeywordEntry;

    // ========================================================================
    // Delegate-backed static API (fast path using Delegate.CreateDelegate)
    // ========================================================================

    private static Action<bool, int> _setEnabled;
    private static Func<int> _getRemoteGUID;
    private static Func<Array> _getFrameEvents;
    private static Func<int, string> _getEventInfoName;
    private static Func<int, UnityEngine.Object> _getEventObject;
    private static Func<int> _getCount;
    private static Func<int> _getLimit;
    private static Action<int> _setLimit;

    // ========================================================================
    // MethodInfo for methods with internal parameter types (slow path)
    // ========================================================================

    /// <summary>bool GetFrameEventData(int index, FrameDebuggerEventData data)</summary>
    private static MethodInfo _miGetFrameEventData;

    // ========================================================================
    // Field accessors for FrameDebuggerEventData
    // ========================================================================

    private static FieldInfo _fiOriginalShaderName;
    private static FieldInfo _fiRealShaderName;
    private static FieldInfo _fiPassName;
    private static FieldInfo _fiPassLightMode;
    private static FieldInfo _fiShaderInstanceID;
    private static FieldInfo _fiShaderPassIndex;
    private static FieldInfo _fiShaderKeywords;
    private static FieldInfo _fiSubShaderIndex;
    private static FieldInfo _fiMesh;
    private static FieldInfo _fiVertexCount;
    private static FieldInfo _fiIndexCount;
    private static FieldInfo _fiInstanceCount;
    private static FieldInfo _fiDrawCallCount;
    private static FieldInfo _fiShaderInfo;

    // ========================================================================
    // Field accessors for ShaderInfo (m_ShaderInfo)
    // ========================================================================

    private static FieldInfo _fiShaderInfoKeywords;
    private static FieldInfo _fiKeywordName;
    private static FieldInfo _fiKeywordFlags;
    private static FieldInfo _fiKeywordIsGlobal;
    private static FieldInfo _fiKeywordIsDynamic;

    // ========================================================================
    // FrameDebuggerEvent field accessors
    // ========================================================================

    private static FieldInfo _fiEventIndex;

    // ========================================================================
    // Availability check
    // ========================================================================

    /// <summary>
    /// True if the static constructor successfully resolved all required types.
    /// Check this before calling any other methods.
    /// </summary>
    public static bool IsAvailable => _tUtil != null && _tEventData != null;

    // ========================================================================
    // Static constructor — resolves all types and members once
    // ========================================================================

    static FrameDebuggerReflect()
    {
        try
        {
            var editorAsm = typeof(Editor).Assembly;

            // --- Resolve types ---
            _tUtil = editorAsm.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
            _tEventData = editorAsm.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData");
            _tEvent = editorAsm.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEvent");

            if (_tUtil == null)
            {
                Debug.LogError("[FrameDebuggerReflect] FrameDebuggerUtility type not found.");
                return;
            }
            if (_tEventData == null)
            {
                Debug.LogError("[FrameDebuggerReflect] FrameDebuggerEventData type not found.");
                return;
            }
            if (_tEvent == null)
            {
                Debug.LogWarning("[FrameDebuggerReflect] FrameDebuggerEvent type not found; " +
                    "GetFrameEventIndices() will be unavailable.");
            }

            // Resolve nested ShaderInfo type from event data fields
            _tShaderInfo = ResolveShaderInfoType();
            if (_tShaderInfo == null)
            {
                Debug.LogWarning("[FrameDebuggerReflect] ShaderInfo type not found; " +
                    "detailed keyword extraction will be unavailable.");
            }

            // Resolve keyword entry type from ShaderInfo
            _tKeywordEntry = ResolveKeywordEntryType();

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            // --- Static method delegates ---
            _setEnabled = CreateStaticDelegate<Action<bool, int>>(
                "SetEnabled", flags, new[] { typeof(bool), typeof(int) });
            _getRemoteGUID = CreateStaticDelegate<Func<int>>(
                "GetRemotePlayerGUID", flags);
            _getFrameEvents = CreateStaticDelegate<Func<Array>>(
                "GetFrameEvents", flags);
            _getEventInfoName = CreateStaticDelegate<Func<int, string>>(
                "GetFrameEventInfoName", flags);
            _getEventObject = CreateStaticDelegate<Func<int, UnityEngine.Object>>(
                "GetFrameEventObject", flags);

            // --- Property getter/setter delegates ---
            _getCount = CreatePropertyGetter<Func<int>>("count", flags);
            _getLimit = CreatePropertyGetter<Func<int>>("limit", flags);
            _setLimit = CreatePropertySetter<Action<int>>("limit", flags);

            // --- GetFrameEventData (internal param type -> MethodInfo.Invoke) ---
            _miGetFrameEventData = _tUtil.GetMethod(
                "GetFrameEventData", flags, null,
                new[] { typeof(int), _tEventData }, null);

            // --- FrameDebuggerEventData instance fields ---
            var fi = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _fiOriginalShaderName = _tEventData.GetField("m_OriginalShaderName", fi);
            _fiRealShaderName     = _tEventData.GetField("m_RealShaderName", fi);
            _fiPassName           = _tEventData.GetField("m_PassName", fi);
            _fiPassLightMode      = _tEventData.GetField("m_PassLightMode", fi);
            _fiShaderInstanceID   = _tEventData.GetField("m_ShaderInstanceID", fi);
            _fiShaderPassIndex    = _tEventData.GetField("m_ShaderPassIndex", fi);
            _fiShaderKeywords     = _tEventData.GetField("shaderKeywords", fi);
            _fiSubShaderIndex     = _tEventData.GetField("m_SubShaderIndex", fi);
            _fiMesh               = _tEventData.GetField("m_Mesh", fi);
            _fiVertexCount        = _tEventData.GetField("m_VertexCount", fi);
            _fiIndexCount         = _tEventData.GetField("m_IndexCount", fi);
            _fiInstanceCount      = _tEventData.GetField("m_InstanceCount", fi);
            _fiDrawCallCount      = _tEventData.GetField("m_DrawCallCount", fi);
            _fiShaderInfo         = _tEventData.GetField("m_ShaderInfo", fi);

            // --- ShaderInfo instance fields ---
            if (_tShaderInfo != null)
            {
                _fiShaderInfoKeywords = _tShaderInfo.GetField("m_Keywords", fi);
            }

            // --- Keyword entry instance fields ---
            if (_tKeywordEntry != null)
            {
                _fiKeywordName      = _tKeywordEntry.GetField("m_Name", fi);
                _fiKeywordFlags     = _tKeywordEntry.GetField("m_Flags", fi);
                _fiKeywordIsGlobal  = _tKeywordEntry.GetField("m_IsGlobal", fi);
                _fiKeywordIsDynamic = _tKeywordEntry.GetField("m_IsDynamic", fi);
            }

            // --- FrameDebuggerEvent instance fields ---
            if (_tEvent != null)
            {
                _fiEventIndex = _tEvent.GetField("index", fi);
            }

            Debug.Log("[FrameDebuggerReflect] Initialized successfully.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[FrameDebuggerReflect] Initialization failed: {e.Message}");
        }
    }

    // ========================================================================
    // Public API — FrameDebugger control
    // ========================================================================

    public static void SetEnabled(bool enable, int guid)
    {
        if (_setEnabled == null)
        {
            Debug.LogWarning("[FrameDebuggerReflect] SetEnabled delegate not available.");
            return;
        }
        _setEnabled(enable, guid);
    }

    public static int GetRemotePlayerGUID()
    {
        return _getRemoteGUID != null ? _getRemoteGUID() : -1;
    }

    public static int GetCount()
    {
        return _getCount != null ? _getCount() : 0;
    }

    public static int GetLimit()
    {
        return _getLimit != null ? _getLimit() : 0;
    }

    public static void SetLimit(int value)
    {
        if (_setLimit == null)
        {
            Debug.LogWarning("[FrameDebuggerReflect] SetLimit delegate not available.");
            return;
        }
        _setLimit(value);
    }

    /// <summary>
    /// Returns the raw FrameDebuggerEvent array, or null if unavailable.
    /// Each element is an internal FrameDebuggerEvent struct.
    /// </summary>
    public static Array GetFrameEvents()
    {
        return _getFrameEvents?.Invoke();
    }

    public static string GetFrameEventInfoName(int index)
    {
        return _getEventInfoName != null ? _getEventInfoName(index) : string.Empty;
    }

    public static UnityEngine.Object GetFrameEventObject(int index)
    {
        return _getEventObject?.Invoke(index);
    }

    // ========================================================================
    // Public API — Event data
    // ========================================================================

    /// <summary>
    /// Populates the given event data object for the specified event index.
    /// The data object must have been created via CreateEventData().
    /// Returns true if data was successfully populated (rendering events only;
    /// non-rendering events like "Clear" return false).
    ///
    /// IMPORTANT: SetLimit(index + 1) must be called before calling this,
    /// AND one EditorApplication.update tick must elapse for data to populate.
    /// </summary>
    public static bool GetFrameEventData(int index, object data)
    {
        if (_miGetFrameEventData == null) return false;
        if (data == null)
        {
            Debug.LogWarning("[FrameDebuggerReflect] GetFrameEventData: data is null.");
            return false;
        }
        try
        {
            return (bool)_miGetFrameEventData.Invoke(null, new[] { index, data });
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] GetFrameEventData failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates a new instance of the internal FrameDebuggerEventData type.
    /// Use this to obtain a data container for GetFrameEventData().
    /// Returns null if the type is not available or instantiation fails.
    /// </summary>
    public static object CreateEventData()
    {
        if (_tEventData == null) return null;
        try
        {
            return Activator.CreateInstance(_tEventData, true);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] CreateEventData failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extracts the index values from the GetFrameEvents() array.
    /// Each element is a FrameDebuggerEvent struct with an 'index' field.
    /// Returns an empty array if the event type or index field is not available.
    /// </summary>
    public static int[] GetFrameEventIndices()
    {
        var events = GetFrameEvents();
        if (events == null || events.Length == 0 || _fiEventIndex == null)
            return Array.Empty<int>();

        int[] indices = new int[events.Length];
        for (int i = 0; i < events.Length; i++)
        {
            try
            {
                indices[i] = (int)_fiEventIndex.GetValue(events.GetValue(i));
            }
            catch
            {
                indices[i] = -1;
            }
        }
        return indices;
    }

    // ========================================================================
    // Public API — FrameDebuggerEventData field extractors
    // ========================================================================

    public static string GetOriginalShaderName(object data)
    {
        return _fiOriginalShaderName?.GetValue(data) as string ?? string.Empty;
    }

    public static string GetRealShaderName(object data)
    {
        return _fiRealShaderName?.GetValue(data) as string ?? string.Empty;
    }

    public static string GetPassName(object data)
    {
        return _fiPassName?.GetValue(data) as string ?? string.Empty;
    }

    public static string GetPassLightMode(object data)
    {
        return _fiPassLightMode?.GetValue(data) as string ?? string.Empty;
    }

    public static int GetShaderInstanceID(object data)
    {
        if (_fiShaderInstanceID == null || data == null) return 0;
        return (int)(_fiShaderInstanceID.GetValue(data) ?? 0);
    }

    public static int GetShaderPassIndex(object data)
    {
        if (_fiShaderPassIndex == null || data == null) return -1;
        return (int)(_fiShaderPassIndex.GetValue(data) ?? -1);
    }

    public static string GetShaderKeywords(object data)
    {
        return _fiShaderKeywords?.GetValue(data) as string ?? string.Empty;
    }

    public static int GetSubShaderIndex(object data)
    {
        if (_fiSubShaderIndex == null || data == null) return 0;
        return (int)(_fiSubShaderIndex.GetValue(data) ?? 0);
    }

    public static Mesh GetMesh(object data)
    {
        return _fiMesh?.GetValue(data) as Mesh;
    }

    public static int GetVertexCount(object data)
    {
        if (_fiVertexCount == null || data == null) return -1;
        return (int)(_fiVertexCount.GetValue(data) ?? -1);
    }

    public static int GetIndexCount(object data)
    {
        if (_fiIndexCount == null || data == null) return -1;
        return (int)(_fiIndexCount.GetValue(data) ?? -1);
    }

    public static int GetInstanceCount(object data)
    {
        if (_fiInstanceCount == null || data == null) return -1;
        return (int)(_fiInstanceCount.GetValue(data) ?? -1);
    }

    public static int GetDrawCallCount(object data)
    {
        if (_fiDrawCallCount == null || data == null) return -1;
        return (int)(_fiDrawCallCount.GetValue(data) ?? -1);
    }

    /// <summary>
    /// Returns the raw m_ShaderInfo object from the event data, or null.
    /// Use ExtractShaderInfoKeywords() to parse keyword entries.
    /// </summary>
    public static object GetShaderInfo(object data)
    {
        return _fiShaderInfo?.GetValue(data);
    }

    // ========================================================================
    // Public API — ShaderInfo keyword extraction
    // ========================================================================

    /// <summary>
    /// Extracts detailed keyword entries from m_ShaderInfo.m_Keywords[].
    /// Returns null if ShaderInfo or keyword fields are not available.
    /// </summary>
    public static List<FrameDebuggerRawKeyword> ExtractShaderInfoKeywords(object eventData)
    {
        if (_fiShaderInfo == null || _fiShaderInfoKeywords == null ||
            _fiKeywordName == null || eventData == null)
            return null;

        try
        {
            object shaderInfo = _fiShaderInfo.GetValue(eventData);
            if (shaderInfo == null) return null;

            Array keywordsArray = _fiShaderInfoKeywords.GetValue(shaderInfo) as Array;
            if (keywordsArray == null || keywordsArray.Length == 0)
                return new List<FrameDebuggerRawKeyword>(0);

            var list = new List<FrameDebuggerRawKeyword>(keywordsArray.Length);
            for (int i = 0; i < keywordsArray.Length; i++)
            {
                object entry = keywordsArray.GetValue(i);
                if (entry == null) continue;

                var kw = new FrameDebuggerRawKeyword
                {
                    name      = _fiKeywordName.GetValue(entry) as string ?? string.Empty,
                    flags     = (int)(_fiKeywordFlags?.GetValue(entry) ?? 0),
                    isGlobal  = (bool)(_fiKeywordIsGlobal?.GetValue(entry) ?? false),
                    isDynamic = (bool)(_fiKeywordIsDynamic?.GetValue(entry) ?? false),
                };
                list.Add(kw);
            }

            return list;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] ExtractShaderInfoKeywords failed: {e.Message}");
            return null;
        }
    }

    // ========================================================================
    // Public API — Convenience: extract everything from one event
    // ========================================================================

    /// <summary>
    /// Captures a single event at the given index and returns a fully populated
    /// FrameDebuggerRawEvent. Requires that SetLimit(index + 1) was already called
    /// AND one editor frame has elapsed for data to populate.
    /// Returns null if data is not available for this event.
    /// </summary>
    public static FrameDebuggerRawEvent CaptureRawEvent(int index)
    {
        object data = CreateEventData();
        if (data == null) return null;

        bool hasData = GetFrameEventData(index, data);

        var shader = EditorUtility.InstanceIDToObject(GetShaderInstanceID(data)) as Shader;

        var raw = new FrameDebuggerRawEvent
        {
            index          = index,
            name           = GetFrameEventInfoName(index),
            shader         = shader,
            shaderName     = GetOriginalShaderName(data),
            realShaderName = GetRealShaderName(data),
            passIndex      = GetShaderPassIndex(data),
            subShaderIndex = GetSubShaderIndex(data),
            passName       = GetPassName(data),
            lightMode      = GetPassLightMode(data),
            shaderKeywords = GetShaderKeywords(data),
            vertexCount    = GetVertexCount(data),
            indexCount     = GetIndexCount(data),
            instanceCount  = GetInstanceCount(data),
            drawCallCount  = GetDrawCallCount(data),
            mesh           = GetMesh(data),
            hasData        = hasData,
        };

        // Resolve shader asset path and GUID
        if (raw.shader != null)
        {
            raw.shaderPath = AssetDatabase.GetAssetPath(raw.shader);
            raw.shaderGuid = AssetDatabase.AssetPathToGUID(raw.shaderPath);
        }

        // Extract detailed keyword info from m_ShaderInfo
        if (hasData)
        {
            raw.rawKeywords = ExtractShaderInfoKeywords(data);
        }

        return raw;
    }

    // ========================================================================
    // Async Capture Loop
    // ========================================================================

    /// <summary>
    /// Starts an asynchronous capture of all FrameDebugger events.
    /// Internally uses EditorApplication.update to advance through events one per frame.
    /// Calls onDone with the captured events when complete.
    ///
    /// IMPORTANT: FrameDebugger must already be enabled (SetEnabled(true, guid)) before
    /// calling this method. The method will not enable or disable it.
    ///
    /// Example:
    ///   int guid = FrameDebuggerReflect.GetRemotePlayerGUID();
    ///   FrameDebuggerReflect.SetEnabled(true, guid);
    ///   FrameDebuggerReflect.StartCapture(events => {
    ///       Debug.Log($"Captured {events.Count} events");
    ///       FrameDebuggerReflect.SetEnabled(false, guid);
    ///   });
    /// </summary>
    public static void StartCapture(Action<List<FrameDebuggerRawEvent>> onDone)
    {
        if (!IsAvailable)
        {
            Debug.LogError("[FrameDebuggerReflect] StartCapture: Reflection types not available.");
            onDone?.Invoke(new List<FrameDebuggerRawEvent>(0));
            return;
        }

        var result = new List<FrameDebuggerRawEvent>();
        int targetIndex = 0;
        int eventCount = 0;
        bool limitJustSet = false;

        void Tick()
        {
            // Phase 1: Wait for events to populate
            if (eventCount <= 0)
            {
                var events = GetFrameEvents();
                if (events != null && events.Length > 0)
                {
                    eventCount = events.Length;
                }
                else
                {
                    eventCount = GetCount();
                }

                if (eventCount <= 0)
                {
                    // Force repaint to trigger event population
                    InternalEditorUtility.RepaintAllViews();
                    return;
                }

                Debug.Log($"[FrameDebuggerReflect] Found {eventCount} events. Starting capture...");

                // Set limit to 1 to populate data for the first event
                SetLimit(1);
                limitJustSet = true;
                return;
            }

            // Phase 2: After setting limit, wait one frame for data to populate
            if (limitJustSet)
            {
                limitJustSet = false;
                return;
            }

            // Phase 3: Capture event at current limit-1
            int curIndex = GetLimit() - 1;

            var raw = CaptureRawEvent(curIndex);
            if (raw != null)
            {
                result.Add(raw);
                if (raw.hasData && (result.Count <= 30))
                {
                    Debug.Log($"[FrameDebuggerReflect] #{curIndex}: shader='{raw.shaderName}', " +
                        $"pass='{raw.passName}', lightMode='{raw.lightMode}', " +
                        $"passIdx={raw.passIndex}, kw='{raw.shaderKeywords}'");
                }
            }

            targetIndex++;

            // Phase 4: Done or advance to next event
            if (targetIndex >= eventCount)
            {
                EditorApplication.update -= Tick;
                Debug.Log($"[FrameDebuggerReflect] Capture complete. " +
                    $"{result.Count} events captured ({(result.Count > 0 ? (float)result.FindAll(e => e.hasData).Count / result.Count * 100 : 0):F0}% with data).");
                onDone?.Invoke(result);
                return;
            }

            SetLimit(targetIndex + 1);
            limitJustSet = true;
        }

        EditorApplication.update += Tick;
    }

    // ========================================================================
    // Reflection helpers
    // ========================================================================

    /// <summary>
    /// Creates a delegate for a static method, using Delegate.CreateDelegate for
    /// optimal performance (~10x faster than MethodInfo.Invoke).
    /// </summary>
    private static T CreateStaticDelegate<T>(string methodName, BindingFlags flags, Type[] paramTypes = null)
        where T : Delegate
    {
        if (_tUtil == null) return null;
        MethodInfo method;
        if (paramTypes != null)
        {
            method = _tUtil.GetMethod(methodName, flags, null, paramTypes, null);
        }
        else
        {
            method = _tUtil.GetMethod(methodName, flags);
        }
        if (method == null)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Method '{methodName}' not found on FrameDebuggerUtility.");
            return null;
        }
        try
        {
            return (T)(object)Delegate.CreateDelegate(typeof(T), method);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Failed to create delegate for '{methodName}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates a property getter delegate using Delegate.CreateDelegate.
    /// </summary>
    private static T CreatePropertyGetter<T>(string propName, BindingFlags flags)
        where T : Delegate
    {
        if (_tUtil == null) return null;
        var prop = _tUtil.GetProperty(propName, flags);
        if (prop == null)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Property '{propName}' not found.");
            return null;
        }
        var method = prop.GetGetMethod(true);
        if (method == null) return null;
        try
        {
            return (T)(object)Delegate.CreateDelegate(typeof(T), method);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Failed to create getter delegate for '{propName}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates a property setter delegate using Delegate.CreateDelegate.
    /// </summary>
    private static T CreatePropertySetter<T>(string propName, BindingFlags flags)
        where T : Delegate
    {
        if (_tUtil == null) return null;
        var prop = _tUtil.GetProperty(propName, flags);
        if (prop == null)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Property '{propName}' not found.");
            return null;
        }
        var method = prop.GetSetMethod(true);
        if (method == null) return null;
        try
        {
            return (T)(object)Delegate.CreateDelegate(typeof(T), method);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Failed to create setter delegate for '{propName}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves the ShaderInfo nested type by examining m_ShaderInfo field type
    /// on FrameDebuggerEventData.
    /// </summary>
    private static Type ResolveShaderInfoType()
    {
        if (_tEventData == null) return null;
        try
        {
            var fi = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var field = _tEventData.GetField("m_ShaderInfo", fi);
            return field?.FieldType;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the keyword entry struct type from ShaderInfo.m_Keywords array element type.
    /// </summary>
    private static Type ResolveKeywordEntryType()
    {
        if (_tShaderInfo == null) return null;
        try
        {
            var fi = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var field = _tShaderInfo.GetField("m_Keywords", fi);
            if (field == null) return null;
            var arrayType = field.FieldType;
            if (arrayType == null || !arrayType.IsArray) return null;
            return arrayType.GetElementType();
        }
        catch
        {
            return null;
        }
    }
}
