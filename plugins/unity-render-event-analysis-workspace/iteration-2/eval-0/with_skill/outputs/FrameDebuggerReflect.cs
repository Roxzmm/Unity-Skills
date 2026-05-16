using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// Reflection-based wrapper around Unity's internal FrameDebuggerUtility API.
///
/// Uses Delegate.CreateDelegate for ~10x faster static method/property access
/// compared to MethodInfo.Invoke. Does NOT depend on InternalAPIEditorBridge.
///
/// --- How the FrameDebugger Capture Flow Works ---
///
/// 1. Enable FrameDebugger via SetEnabled(true, guid)
/// 2. Wait for events to populate (call RepaintAllViews + editor frames)
/// 3. For each event i:
///    a. Set limit = i + 1  (THIS controls which event's data is populated)
///    b. Wait 1 editor frame
///    c. Call GetFrameEventData(i, data) to extract shader/pass/keyword info
/// 4. Disable FrameDebugger
///
/// The `limit` property is CRITICAL: GetFrameEventData only returns valid data
/// for the event at (limit - 1). You must set limit per-event.
/// </summary>
public static class FrameDebuggerReflect
{
    // -----------------------------------------------------------------------
    //  Resolved types
    // -----------------------------------------------------------------------
    private static Type _tUtil;          // UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility
    private static Type _tEventData;     // UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData

    // -----------------------------------------------------------------------
    //  Delegate-backed API access (~10x faster than MethodInfo.Invoke)
    // -----------------------------------------------------------------------

    // Static methods
    private static Action<bool, int> _setEnabled;
    private static Func<int> _getRemoteGUID;
    private static Func<Array> _getFrameEvents;
    private static Func<int, string> _getEventInfoName;
    private static Func<int, UnityEngine.Object> _getEventObject;

    // Static properties
    private static Func<int> _getCount;
    private static Func<int> _getLimit;
    private static Action<int> _setLimit;

    // -----------------------------------------------------------------------
    //  MethodInfo for GetFrameEventData (must use Invoke — param type is internal)
    // -----------------------------------------------------------------------
    private static MethodInfo _miGetFrameEventData;

    // -----------------------------------------------------------------------
    //  FieldInfo for FrameDebuggerEventData fields
    // -----------------------------------------------------------------------
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

    // -----------------------------------------------------------------------
    //  Availability check
    // -----------------------------------------------------------------------
    /// <summary>True if all internal types were resolved successfully.</summary>
    public static bool IsAvailable => _tUtil != null && _tEventData != null;

    // -----------------------------------------------------------------------
    //  Static constructor — one-time reflection initialization
    // -----------------------------------------------------------------------
    static FrameDebuggerReflect()
    {
        try
        {
            var editorAsm = typeof(Editor).Assembly;

            _tUtil = editorAsm.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
            _tEventData = editorAsm.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData");

            if (_tUtil == null)
            {
                Debug.LogError("[FrameDebuggerReflect] Cannot find type: " +
                    "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
                return;
            }
            if (_tEventData == null)
            {
                Debug.LogError("[FrameDebuggerReflect] Cannot find type: " +
                    "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData");
                return;
            }

            var staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            // ---- Static method delegates ----
            _setEnabled = CreateStaticDelegate<Action<bool, int>>(
                "SetEnabled", staticFlags, new[] { typeof(bool), typeof(int) });
            _getRemoteGUID = CreateStaticDelegate<Func<int>>(
                "GetRemotePlayerGUID", staticFlags);
            _getFrameEvents = CreateStaticDelegate<Func<Array>>(
                "GetFrameEvents", staticFlags);
            _getEventInfoName = CreateStaticDelegate<Func<int, string>>(
                "GetFrameEventInfoName", staticFlags);
            _getEventObject = CreateStaticDelegate<Func<int, UnityEngine.Object>>(
                "GetFrameEventObject", staticFlags);

            // ---- Property getter/setter delegates ----
            _getCount = CreatePropertyGetter<Func<int>>("count", staticFlags);
            _getLimit = CreatePropertyGetter<Func<int>>("limit", staticFlags);
            _setLimit = CreatePropertySetter<Action<int>>("limit", staticFlags);

            // ---- GetFrameEventData: internal parameter type requires MethodInfo.Invoke ----
            _miGetFrameEventData = _tUtil.GetMethod(
                "GetFrameEventData", staticFlags, null,
                new[] { typeof(int), _tEventData }, null);

            // ---- Instance field accessors ----
            var instFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _fiOriginalShaderName = _tEventData.GetField("m_OriginalShaderName", instFlags);
            _fiRealShaderName    = _tEventData.GetField("m_RealShaderName", instFlags);
            _fiPassName          = _tEventData.GetField("m_PassName", instFlags);
            _fiPassLightMode     = _tEventData.GetField("m_PassLightMode", instFlags);
            _fiShaderInstanceID  = _tEventData.GetField("m_ShaderInstanceID", instFlags);
            _fiShaderPassIndex   = _tEventData.GetField("m_ShaderPassIndex", instFlags);
            _fiShaderKeywords    = _tEventData.GetField("shaderKeywords", instFlags);
            _fiSubShaderIndex    = _tEventData.GetField("m_SubShaderIndex", instFlags);
            _fiMesh              = _tEventData.GetField("m_Mesh", instFlags);
            _fiVertexCount       = _tEventData.GetField("m_VertexCount", instFlags);
            _fiIndexCount        = _tEventData.GetField("m_IndexCount", instFlags);
            _fiInstanceCount     = _tEventData.GetField("m_InstanceCount", instFlags);
            _fiDrawCallCount     = _tEventData.GetField("m_DrawCallCount", instFlags);

            Debug.Log("[FrameDebuggerReflect] Initialized successfully.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[FrameDebuggerReflect] Initialization failed: {e.Message}");
        }
    }

    // =======================================================================
    //  PUBLIC API
    // =======================================================================

    // -----------------------------------------------------------------------
    //  FrameDebugger lifecycle
    // -----------------------------------------------------------------------

    /// <summary>Enable or disable the Frame Debugger.</summary>
    /// <param name="enable">True to enable, false to disable.</param>
    /// <param name="guid">
    ///   Remote player GUID. Use -1 for local Editor.
    ///   Obtain via <see cref="GetRemotePlayerGUID"/>.
    /// </param>
    public static void SetEnabled(bool enable, int guid = -1)
        => _setEnabled?.Invoke(enable, guid);

    /// <summary>
    /// Enable FrameDebugger cleanly (disables first if already enabled).
    /// </summary>
    public static void Enable()
    {
        if (!IsAvailable) return;
        int guid = GetRemotePlayerGUID();
        SetEnabled(false, guid);
        SetEnabled(true, guid);
    }

    /// <summary>Disable FrameDebugger.</summary>
    public static void Disable()
    {
        if (!IsAvailable) return;
        SetEnabled(false, GetRemotePlayerGUID());
    }

    /// <summary>GUID of the remote player. Returns -1 for local Editor.</summary>
    public static int GetRemotePlayerGUID()
        => _getRemoteGUID != null ? _getRemoteGUID() : -1;

    // -----------------------------------------------------------------------
    //  Event count
    // -----------------------------------------------------------------------

    /// <summary>
    /// Number of captured frame events.
    /// Call <see cref="GetFrameEvents"/> for the array-based count as well.
    /// </summary>
    public static int GetCount()
        => _getCount != null ? _getCount() : 0;

    /// <summary>
    /// Returns the raw FrameDebuggerEvent[] for the current frame.
    /// May be null or empty until the FrameDebugger populates events.
    /// Use the Length of the returned array for event count.
    /// </summary>
    public static Array GetFrameEvents()
        => _getFrameEvents?.Invoke();

    // -----------------------------------------------------------------------
    //  Event limit (controls which event's data is populated)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Current limit value. GetFrameEventData returns valid data only for event at (limit - 1).
    /// </summary>
    public static int GetLimit()
        => _getLimit != null ? _getLimit() : 0;

    /// <summary>
    /// Set the limit to populate data for event index (limit - 1).
    /// After setting, wait one editor frame before calling GetFrameEventData.
    /// </summary>
    public static void SetLimit(int value)
    {
        if (_setLimit != null)
            _setLimit(value);
    }

    // -----------------------------------------------------------------------
    //  Event metadata (works without setting limit)
    // -----------------------------------------------------------------------

    /// <summary>Display name of the event (e.g., "Draw Mesh", "Clear").</summary>
    public static string GetEventInfoName(int index)
        => _getEventInfoName != null ? _getEventInfoName(index) : string.Empty;

    /// <summary>UnityEngine.Object associated with the event (often the Renderer).</summary>
    public static UnityEngine.Object GetEventObject(int index)
        => _getEventObject?.Invoke(index);

    // -----------------------------------------------------------------------
    //  Per-event data extraction (requires limit to be set first)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Populate a FrameDebuggerEventData object with data for the given event index.
    /// Must have set limit = index + 1 and waited one editor frame first.
    /// </summary>
    /// <param name="eventIndex">Zero-based event index.</param>
    /// <param name="data">Event data object created via <see cref="CreateEventData"/>.</param>
    /// <returns>True if data was populated successfully.</returns>
    public static bool GetFrameEventData(int eventIndex, object data)
    {
        if (_miGetFrameEventData == null || data == null)
            return false;
        return (bool)_miGetFrameEventData.Invoke(null, new[] { eventIndex, data });
    }

    /// <summary>
    /// Create an instance of the internal FrameDebuggerEventData type.
    /// Pass the result to <see cref="GetFrameEventData"/>.
    /// </summary>
    public static object CreateEventData()
    {
        try { return Activator.CreateInstance(_tEventData, true); }
        catch { return null; }
    }

    // -----------------------------------------------------------------------
    //  Field extractors — safe with null data
    // -----------------------------------------------------------------------

    public static string GetOriginalShaderName(object data)
        => _fiOriginalShaderName?.GetValue(data) as string ?? string.Empty;

    public static string GetRealShaderName(object data)
        => _fiRealShaderName?.GetValue(data) as string ?? string.Empty;

    public static string GetPassName(object data)
        => _fiPassName?.GetValue(data) as string ?? string.Empty;

    public static string GetPassLightMode(object data)
        => _fiPassLightMode?.GetValue(data) as string ?? string.Empty;

    public static int GetShaderInstanceID(object data)
        => (int)(_fiShaderInstanceID?.GetValue(data) ?? 0);

    public static int GetShaderPassIndex(object data)
        => (int)(_fiShaderPassIndex?.GetValue(data) ?? -1);

    public static string GetShaderKeywords(object data)
        => _fiShaderKeywords?.GetValue(data) as string ?? string.Empty;

    public static int GetSubShaderIndex(object data)
        => (int)(_fiSubShaderIndex?.GetValue(data) ?? 0);

    public static UnityEngine.Object GetMesh(object data)
        => _fiMesh?.GetValue(data) as UnityEngine.Object;

    public static int GetVertexCount(object data)
        => (int)(_fiVertexCount?.GetValue(data) ?? 0);

    public static int GetIndexCount(object data)
        => (int)(_fiIndexCount?.GetValue(data) ?? 0);

    public static int GetInstanceCount(object data)
        => (int)(_fiInstanceCount?.GetValue(data) ?? 0);

    public static int GetDrawCallCount(object data)
        => (int)(_fiDrawCallCount?.GetValue(data) ?? 0);

    // =======================================================================
    //  HIGH-LEVEL CAPTURE
    // =======================================================================

    /// <summary>
    /// Capture all frame events synchronously into a list of FrameDebuggerRawEvent.
    ///
    /// This method MUST be called from within an EditorApplication.update callback
    /// because the FrameDebugger needs editor ticks to populate data after each
    /// limit change. The async version <see cref="StartCapture"/> handles this for you.
    /// </summary>
    /// <param name="onDone">Callback invoked with the captured event list.</param>
    public static void StartCapture(Action<List<FrameDebuggerRawEvent>> onDone)
    {
        if (!IsAvailable)
        {
            Debug.LogError("[FrameDebuggerReflect] Cannot start capture: reflection init failed.");
            onDone?.Invoke(new List<FrameDebuggerRawEvent>());
            return;
        }

        var result = new List<FrameDebuggerRawEvent>();
        int targetIndex = 0;
        int eventCount = 0;
        bool limitSet = false;

        void Tick()
        {
            // ---- Phase 1: Wait for events to populate ----
            if (eventCount <= 0)
            {
                var events = GetFrameEvents();
                eventCount = events != null && events.Length > 0
                    ? events.Length
                    : GetCount();

                if (eventCount <= 0)
                {
                    // Trigger repaint to coax FrameDebugger into populating events
                    InternalEditorUtility.RepaintAllViews();
                    return;
                }

                Debug.Log($"[FrameDebuggerReflect] Found {eventCount} events. Starting capture...");
                SetLimit(1); // Start with first event
                limitSet = true;
                return;
            }

            // ---- Phase 2: Capture current event ----
            int curIndex = GetLimit() - 1;

            var rawEvent = CaptureSingleEvent(curIndex);
            if (rawEvent != null)
            {
                result.Add(rawEvent);
                Debug.Log($"[FrameDebuggerReflect] Captured #{curIndex}: {rawEvent.shaderName} / {rawEvent.passName}");
            }
            else
            {
                // Non-rendering event (Clear, etc.) — still record with index
                result.Add(new FrameDebuggerRawEvent
                {
                    index = curIndex,
                    name = GetEventInfoName(curIndex),
                });
            }

            targetIndex++;

            // ---- Phase 3: Done or advance ----
            if (targetIndex >= eventCount)
            {
                EditorApplication.update -= Tick;
                Debug.Log($"[FrameDebuggerReflect] Capture complete. {result.Count} events captured.");
                onDone?.Invoke(result);
                return;
            }

            SetLimit(targetIndex + 1);
        }

        EditorApplication.update += Tick;
    }

    /// <summary>
    /// Synchronously capture all frame events.
    /// WARNING: This blocks the editor. Prefer <see cref="StartCapture"/> for non-blocking use.
    /// Only use this in test code or batch-mode scripts.
    /// </summary>
    public static List<FrameDebuggerRawEvent> CaptureAllEventsBlocking(int maxFramesToWait = 600)
    {
        if (!IsAvailable)
        {
            Debug.LogError("[FrameDebuggerReflect] Cannot capture: reflection init failed.");
            return new List<FrameDebuggerRawEvent>();
        }

        // Enable FrameDebugger
        Enable();

        var result = new List<FrameDebuggerRawEvent>();
        int eventCount = 0;
        int frameWait = 0;

        // Phase 1: Wait for events
        while (frameWait < maxFramesToWait)
        {
            var events = GetFrameEvents();
            eventCount = events != null && events.Length > 0
                ? events.Length
                : GetCount();

            if (eventCount > 0) break;

            InternalEditorUtility.RepaintAllViews();
            EditorApplication.Step();
            frameWait++;
        }

        if (eventCount <= 0)
        {
            Debug.LogWarning("[FrameDebuggerReflect] No events captured within wait limit.");
            Disable();
            return result;
        }

        Debug.Log($"[FrameDebuggerReflect] Found {eventCount} events.");

        // Phase 2: Capture each event
        for (int i = 0; i < eventCount; i++)
        {
            SetLimit(i + 1);

            // Wait one editor frame for data to populate
            for (int w = 0; w < 3; w++)
                EditorApplication.Step();

            var rawEvent = CaptureSingleEvent(i);
            if (rawEvent != null)
                result.Add(rawEvent);
            else
                result.Add(new FrameDebuggerRawEvent
                {
                    index = i,
                    name = GetEventInfoName(i),
                });
        }

        Disable();
        Debug.Log($"[FrameDebuggerReflect] Capture complete. {result.Count} events.");
        return result;
    }

    // =======================================================================
    //  INTERNAL
    // =======================================================================

    /// <summary>
    /// Capture a single event at the given index.
    /// Requires that SetLimit(index + 1) was called and at least one editor frame
    /// has elapsed before calling this.
    /// </summary>
    private static FrameDebuggerRawEvent CaptureSingleEvent(int index)
    {
        object data = CreateEventData();
        if (data == null) return null;

        bool hasData = GetFrameEventData(index, data);
        if (!hasData) return null;

        int instanceId = GetShaderInstanceID(data);
        Shader shader = EditorUtility.InstanceIDToObject(instanceId) as Shader;

        var raw = new FrameDebuggerRawEvent
        {
            index = index,
            name = GetEventInfoName(index),
            shader = shader,
            shaderName = GetOriginalShaderName(data),
            passIndex = GetShaderPassIndex(data),
            passName = GetPassName(data),
            lightMode = GetPassLightMode(data),
            shaderKeywords = GetShaderKeywords(data),
        };

        if (raw.shader != null)
        {
            raw.shaderPath = AssetDatabase.GetAssetPath(raw.shader);
            raw.shaderGuid = AssetDatabase.AssetPathToGUID(raw.shaderPath);
        }

        return raw;
    }

    // =======================================================================
    //  REFLECTION HELPERS
    // =======================================================================

    /// <summary>
    /// Create a delegate for a static method.
    /// ~10x faster than calling MethodInfo.Invoke() each time.
    /// </summary>
    private static T CreateStaticDelegate<T>(
        string methodName, BindingFlags flags, Type[] paramTypes = null)
        where T : Delegate
    {
        MethodInfo method = paramTypes != null
            ? _tUtil.GetMethod(methodName, flags, null, paramTypes, null)
            : _tUtil.GetMethod(methodName, flags);

        if (method == null)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Method '{methodName}' not found.");
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
    /// Create a property getter delegate.
    /// Returns default(T) if property or getter is not found.
    /// </summary>
    private static T CreatePropertyGetter<T>(string propName, BindingFlags flags)
        where T : Delegate
    {
        var prop = _tUtil.GetProperty(propName, flags);
        if (prop == null)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Property '{propName}' not found.");
            return null;
        }

        var method = prop.GetGetMethod(true);
        if (method == null)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Property '{propName}' getter not found.");
            return null;
        }

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
    /// Create a property setter delegate.
    /// Returns default(T) if property or setter is not found.
    /// </summary>
    private static T CreatePropertySetter<T>(string propName, BindingFlags flags)
        where T : Delegate
    {
        var prop = _tUtil.GetProperty(propName, flags);
        if (prop == null)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Property '{propName}' not found.");
            return null;
        }

        var method = prop.GetSetMethod(true);
        if (method == null)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Property '{propName}' setter not found.");
            return null;
        }

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
}
