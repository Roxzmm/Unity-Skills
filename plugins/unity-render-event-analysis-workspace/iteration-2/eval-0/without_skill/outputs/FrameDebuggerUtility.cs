using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reflection-based wrapper for UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility.
///
/// Zero dependency on InternalAPIEditorBridge or any custom internal-access assembly.
/// All internal API access is performed via reflection at init time.
///
/// Performance design:
///   - Static methods with only public parameter types use Delegate.CreateDelegate
///     for near-direct-call speed (no Reflection overhead per invocation).
///   - GetFrameEventData(int, internalType) uses MethodInfo.Invoke because the
///     internal parameter type (FrameDebuggerEventData) cannot appear in a public
///     delegate signature.
///   - Field reads on FrameDebuggerEventData use cached FieldInfo (lightweight).
/// </summary>
public static class FrameDebuggerUtility
{
    // ──────────────────────────────────────────────
    // Nested Types
    // ──────────────────────────────────────────────

    /// <summary>
    /// Public-facing snapshot of one FrameDebugger event.
    /// Populated by <see cref="CaptureSingleEvent"/> or <see cref="CaptureAllEvents"/>.
    /// </summary>
    public class FrameEventInfo
    {
        public int index;
        public string name;
        public Shader shader;
        public string originalShaderName;
        public string shaderPath;
        public string shaderGUID;
        public int shaderInstanceID;
        public int passIndex;
        public string passName;
        public string passLightMode;
        public string shaderKeywords;
        public Mesh mesh;
        public int vertexCount;
        public int indexCount;
        public int instanceCount;
        public int drawCallCount;
        public UnityEngine.Object eventObject;
    }

    // ──────────────────────────────────────────────
    // Reflection State
    // ──────────────────────────────────────────────

    private static Type _tUtil;
    private static Type _tEventData;
    private static bool _initialized;
    private static string _initError;

    // Delegate-backed accessors  (Delegate.CreateDelegate ─ near-zero overhead)
    private static Action<bool, int> _setEnabled;
    private static Func<int> _getRemotePlayerGUID;
    private static Func<Array> _getFrameEvents;
    private static Func<int, string> _getEventInfoName;
    private static Func<int, UnityEngine.Object> _getEventObject;
    private static Func<int> _getCount;
    private static Func<int> _getLimit;
    private static Action<int> _setLimit;

    // MethodInfo fallback ─ GetFrameEventData has an internal parameter type,
    // so Delegate.CreateDelegate cannot match it from a public delegate signature.
    private static MethodInfo _miGetFrameEventData;

    // FieldInfo cache ─ reading fields on the internal FrameDebuggerEventData
    private static FieldInfo _fiOriginalShaderName;
    private static FieldInfo _fiPassName;
    private static FieldInfo _fiPassLightMode;
    private static FieldInfo _fiShaderInstanceID;
    private static FieldInfo _fiShaderPassIndex;
    private static FieldInfo _fiShaderKeywords;
    private static FieldInfo _fiMesh;
    private static FieldInfo _fiVertexCount;
    private static FieldInfo _fiIndexCount;
    private static FieldInfo _fiInstanceCount;
    private static FieldInfo _fiDrawCallCount;
    private static FieldInfo _fiSubShaderIndex;

    // ──────────────────────────────────────────────
    // Status
    // ──────────────────────────────────────────────

    /// <summary>True when all required types/members were resolved at init.</summary>
    public static bool IsAvailable => _initialized;

    /// <summary>Human-readable error if init failed, null otherwise.</summary>
    public static string InitError => _initError;

    // ──────────────────────────────────────────────
    // FrameDebugger Lifecycle
    // ──────────────────────────────────────────────

    /// <summary>Enable or disable the Frame Debugger for a specific remote player.</summary>
    public static void SetEnabled(bool enable, int remotePlayerGUID)
    {
        EnsureInitialized();
        _setEnabled?.Invoke(enable, remotePlayerGUID);
    }

    /// <summary>
    /// Shortcut: enable the Frame Debugger for the local player (or the first
    /// remote player if one is active).  The GUID is automatically obtained.
    /// </summary>
    public static void Enable()
    {
        int guid = remotePlayerGUID;
        SetEnabled(true, guid);
    }

    /// <summary>Shortcut: disable the Frame Debugger.</summary>
    public static void Disable()
    {
        SetEnabled(false, remotePlayerGUID);
    }

    /// <summary>GUID of the remote/windowed player being debugged (0 = local).</summary>
    public static int remotePlayerGUID
    {
        get
        {
            EnsureInitialized();
            return _getRemotePlayerGUID != null ? _getRemotePlayerGUID() : 0;
        }
    }

    /// <summary>
    /// Current number of events in the Frame Debugger.
    /// May be 0 before a frame completes after enabling.
    /// </summary>
    public static int eventCount
    {
        get
        {
            EnsureInitialized();
            return _getCount != null ? _getCount() : 0;
        }
    }

    /// <summary>
    /// Get or set the event limit.  The Frame Debugger only captures
    /// events up to this index.  Set to 1 initially, then increment
    /// to walk through events one by one.
    /// </summary>
    public static int limit
    {
        get
        {
            EnsureInitialized();
            return _getLimit != null ? _getLimit() : 0;
        }
        set
        {
            EnsureInitialized();
            if (_setLimit != null) _setLimit(value);
        }
    }

    // ──────────────────────────────────────────────
    // Low-Level Event Access
    // ──────────────────────────────────────────────

    /// <summary>
    /// Create an instance of the internal FrameDebuggerEventData via
    /// Activator.CreateInstance.  Pass the returned object to
    /// <see cref="GetFrameEventData"/>.
    /// </summary>
    public static object CreateEventData()
    {
        EnsureInitialized();
        if (_tEventData == null) return null;
        try { return Activator.CreateInstance(_tEventData, nonPublic: true); }
        catch (Exception ex)
        {
            Debug.LogError($"[FrameDebuggerUtility] Failed to create FrameDebuggerEventData: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Populate <paramref name="data"/> (created via <see cref="CreateEventData"/>)
    /// with information for the event at <paramref name="index"/>.
    ///
    /// NOTE: Uses MethodInfo.Invoke because the second parameter is the internal
    /// FrameDebuggerEventData type, which cannot be expressed in a public delegate.
    /// This is a single invocation per event ─ acceptable overhead.
    /// </summary>
    public static bool GetFrameEventData(int index, object data)
    {
        EnsureInitialized();
        if (_miGetFrameEventData == null || data == null) return false;
        return (bool)_miGetFrameEventData.Invoke(null, new[] { index, data });
    }

    /// <summary>Get the display name of the event at <paramref name="index"/>.</summary>
    public static string GetEventInfoName(int index)
    {
        EnsureInitialized();
        return _getEventInfoName != null ? _getEventInfoName(index) : string.Empty;
    }

    /// <summary>Get the UnityEngine.Object associated with the event at <paramref name="index"/>.</summary>
    public static UnityEngine.Object GetEventObject(int index)
    {
        EnsureInitialized();
        return _getEventObject?.Invoke(index);
    }

    /// <summary>
    /// Get the internal Array of FrameDebuggerEvent structs.
    /// May be empty or null until a frame has completed after enabling.
    /// </summary>
    public static Array GetFrameEvents()
    {
        EnsureInitialized();
        return _getFrameEvents?.Invoke();
    }

    // ──────────────────────────────────────────────
    // Extracting Shader / Pass / Keyword Data
    // ──────────────────────────────────────────────

    /// <summary>Shader name at draw-call submission time.</summary>
    public static string GetOriginalShaderName(object eventData)
        => _fiOriginalShaderName?.GetValue(eventData) as string ?? string.Empty;

    /// <summary>Name of the pass used for this draw call.</summary>
    public static string GetPassName(object eventData)
        => _fiPassName?.GetValue(eventData) as string ?? string.Empty;

    /// <summary>LightMode tag of the pass (e.g. "UniversalForward", "ShadowCaster").</summary>
    public static string GetPassLightMode(object eventData)
        => _fiPassLightMode?.GetValue(eventData) as string ?? string.Empty;

    /// <summary>Index of the pass within the shader (0-based).</summary>
    public static int GetShaderPassIndex(object eventData)
        => (int)(_fiShaderPassIndex?.GetValue(eventData) ?? -1);

    /// <summary>Space-separated shader keywords active for this draw call.</summary>
    public static string GetShaderKeywords(object eventData)
        => _fiShaderKeywords?.GetValue(eventData) as string ?? string.Empty;

    /// <summary>Instance ID of the Shader used (pass to EditorUtility.InstanceIDToObject).</summary>
    public static int GetShaderInstanceID(object eventData)
        => (int)(_fiShaderInstanceID?.GetValue(eventData) ?? 0);

    /// <summary>Sub-shader index within the Shader asset.</summary>
    public static int GetSubShaderIndex(object eventData)
        => (int)(_fiSubShaderIndex?.GetValue(eventData) ?? 0);

    /// <summary>Mesh being rendered (may be null).</summary>
    public static Mesh GetMesh(object eventData)
        => _fiMesh?.GetValue(eventData) as Mesh;

    /// <summary>Vertex count of the draw call.</summary>
    public static int GetVertexCount(object eventData)
        => (int)(_fiVertexCount?.GetValue(eventData) ?? 0);

    /// <summary>Index count of the draw call.</summary>
    public static int GetIndexCount(object eventData)
        => (int)(_fiIndexCount?.GetValue(eventData) ?? 0);

    /// <summary>Instance count (GPU instancing).</summary>
    public static int GetInstanceCount(object eventData)
        => (int)(_fiInstanceCount?.GetValue(eventData) ?? 0);

    /// <summary>Draw-call count within this event.</summary>
    public static int GetDrawCallCount(object eventData)
        => (int)(_fiDrawCallCount?.GetValue(eventData) ?? 0);

    /// <summary>
    /// Resolve the Shader asset from the instance ID stored in eventData.
    /// Returns null if the instance ID is 0 or the asset cannot be found.
    /// </summary>
    public static Shader GetShaderFromEventData(object eventData)
    {
        int id = GetShaderInstanceID(eventData);
        return id != 0 ? EditorUtility.InstanceIDToObject(id) as Shader : null;
    }

    // ──────────────────────────────────────────────
    // High-Level Capture API
    // ──────────────────────────────────────────────

    /// <summary>
    /// Step through every FrameDebugger event via the limit-based walk,
    /// collecting shader/pass/keyword data into a list of <see cref="FrameEventInfo"/>.
    ///
    /// This is an asynchronous multi-frame operation that uses
    /// EditorApplication.update as its driver.  The callback is invoked on the
    /// main thread when all events have been captured.
    /// </summary>
    /// <param name="onComplete">Invoked with the captured event list (may be empty).</param>
    public static void CaptureAllEvents(Action<List<FrameEventInfo>> onComplete)
    {
        EnsureInitialized();

        var results = new List<FrameEventInfo>();
        int totalEvents = 0;
        int currentTarget = 0;

        void Tick()
        {
            // Phase 1 ─ wait for events to become available
            if (totalEvents <= 0)
            {
                var events = _getFrameEvents?.Invoke();
                totalEvents = (events != null && events.Length > 0)
                    ? events.Length
                    : (_getCount != null ? _getCount() : 0);

                if (totalEvents <= 0)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    return;
                }

                // Start with limit=1 so we get the first event
                if (_setLimit != null) _setLimit(1);
                return;
            }

            // Phase 2 ─ capture the current event
            int curIndex = (_getLimit != null ? _getLimit() : 0) - 1;
            var info = CaptureSingleEvent(curIndex);
            if (info != null) results.Add(info);
            currentTarget++;

            // Phase 3 ─ done?
            if (currentTarget >= totalEvents)
            {
                EditorApplication.update -= Tick;
                onComplete?.Invoke(results);
                return;
            }

            // Phase 4 ─ advance to the next event
            if (_setLimit != null) _setLimit(currentTarget + 1);
        }

        EditorApplication.update += Tick;
    }

    /// <summary>
    /// Same as <see cref="CaptureAllEvents"/> but blocks until complete by
    /// looping with <c>EditorApplication.update</c>.
    ///
    /// WARNING: This will stall the Editor for the duration of the capture.
    /// Prefer the async version in production code.
    /// </summary>
    public static List<FrameEventInfo> CaptureAllEventsBlocking()
    {
        var result = new List<FrameEventInfo>();
        bool done = false;

        CaptureAllEvents(list =>
        {
            result = list;
            done = true;
        });

        // Busy-wait (acceptable only because each tick yields to the Editor)
        while (!done)
        {
            EditorApplication.QueuePlayerLoopUpdate();
        }

        return result;
    }

    /// <summary>
    /// Capture a single event at <paramref name="index"/> and return a
    /// populated <see cref="FrameEventInfo"/>, or null on failure.
    /// This is a synchronous call that reads already-captured data.
    /// </summary>
    public static FrameEventInfo CaptureSingleEvent(int index)
    {
        object data = CreateEventData();
        if (data == null) return null;

        bool hasData = GetFrameEventData(index, data);
        if (!hasData) return null;

        var shader = GetShaderFromEventData(data);
        var info = new FrameEventInfo
        {
            index = index,
            name = GetEventInfoName(index),
            shader = shader,
            originalShaderName = GetOriginalShaderName(data),
            passIndex = GetShaderPassIndex(data),
            passName = GetPassName(data),
            passLightMode = GetPassLightMode(data),
            shaderKeywords = GetShaderKeywords(data),
            shaderInstanceID = GetShaderInstanceID(data),
            mesh = GetMesh(data),
            vertexCount = GetVertexCount(data),
            indexCount = GetIndexCount(data),
            instanceCount = GetInstanceCount(data),
            drawCallCount = GetDrawCallCount(data),
            eventObject = GetEventObject(index),
        };

        if (shader != null)
        {
            info.shaderPath = AssetDatabase.GetAssetPath(shader);
            info.shaderGUID = AssetDatabase.AssetPathToGUID(info.shaderPath);
        }

        return info;
    }

    // ──────────────────────────────────────────────
    // Initialization
    // ──────────────────────────────────────────────

    /// <summary>
    /// Run reflection init manually.  Normally this is called lazily on the
    /// first public API invocation, but you can call it early to surface any
    /// errors at a controlled point.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initError = null;

        try
        {
            var editorAsm = typeof(Editor).Assembly;
            _tUtil      = editorAsm.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
            _tEventData = editorAsm.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData");

            if (_tUtil == null)
            {
                _initError = "Type UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility not found.";
                Debug.LogError($"[FrameDebuggerUtility] {_initError}");
                return;
            }
            if (_tEventData == null)
            {
                _initError = "Type UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData not found.";
                Debug.LogError($"[FrameDebuggerUtility] {_initError}");
                return;
            }

            var stc = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            // ── Static methods via Delegate.CreateDelegate ──
            _setEnabled          = CreateStaticDel<Action<bool, int>>("SetEnabled",              stc, typeof(bool), typeof(int));
            _getRemotePlayerGUID = CreateStaticDel<Func<int>>("GetRemotePlayerGUID",             stc);
            _getFrameEvents      = CreateStaticDel<Func<Array>>("GetFrameEvents",                stc);
            _getEventInfoName    = CreateStaticDel<Func<int, string>>("GetFrameEventInfoName",   stc, typeof(int));
            _getEventObject      = CreateStaticDel<Func<int, UnityEngine.Object>>("GetFrameEventObject", stc, typeof(int));

            // ── Static properties via Delegate.CreateDelegate ──
            _getCount = CreatePropGet<Func<int>>("count", stc);
            _getLimit = CreatePropGet<Func<int>>("limit", stc);
            _setLimit = CreatePropSet<Action<int>>("limit", stc);

            // ── GetFrameEventData(int, FrameDebuggerEventData) ──
            // Cannot use Delegate.CreateDelegate because the second parameter
            // is the internal FrameDebuggerEventData type.
            _miGetFrameEventData = _tUtil.GetMethod(
                "GetFrameEventData", stc, null,
                new[] { typeof(int), _tEventData }, null);

            // ── Instance fields on FrameDebuggerEventData ──
            var inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _fiOriginalShaderName = _tEventData.GetField("m_OriginalShaderName", inst);
            _fiPassName           = _tEventData.GetField("m_PassName",           inst);
            _fiPassLightMode      = _tEventData.GetField("m_PassLightMode",      inst);
            _fiShaderInstanceID   = _tEventData.GetField("m_ShaderInstanceID",   inst);
            _fiShaderPassIndex    = _tEventData.GetField("m_ShaderPassIndex",    inst);
            _fiShaderKeywords     = _tEventData.GetField("shaderKeywords",       inst);
            _fiMesh               = _tEventData.GetField("m_Mesh",               inst);
            _fiVertexCount        = _tEventData.GetField("m_VertexCount",        inst);
            _fiIndexCount         = _tEventData.GetField("m_IndexCount",         inst);
            _fiInstanceCount      = _tEventData.GetField("m_InstanceCount",      inst);
            _fiDrawCallCount      = _tEventData.GetField("m_DrawCallCount",      inst);
            _fiSubShaderIndex     = _tEventData.GetField("m_SubShaderIndex",     inst);

            _initialized = true;
        }
        catch (Exception ex)
        {
            _initError = ex.Message;
            _initialized = false;
            Debug.LogError($"[FrameDebuggerUtility] Initialization failed: {ex}");
        }
    }

    // ──────────────────────────────────────────────
    // Initialization Helpers
    // ──────────────────────────────────────────────

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void EnsureInitialized()
    {
        if (!_initialized) Initialize();
    }

    private static T CreateStaticDel<T>(string name, BindingFlags flags, params Type[] paramTypes)
        where T : Delegate
    {
        var mi = paramTypes.Length > 0
            ? _tUtil.GetMethod(name, flags, null, paramTypes, null)
            : _tUtil.GetMethod(name, flags);

        if (mi == null)
        {
            Debug.LogWarning($"[FrameDebuggerUtility] Static method '{name}' not found.");
            return null;
        }
        return (T)(object)Delegate.CreateDelegate(typeof(T), mi);
    }

    private static T CreatePropGet<T>(string prop, BindingFlags flags)
        where T : Delegate
    {
        var p = _tUtil.GetProperty(prop, flags);
        if (p == null) return null;
        var m = p.GetGetMethod(true);
        return m != null ? (T)(object)Delegate.CreateDelegate(typeof(T), m) : null;
    }

    private static T CreatePropSet<T>(string prop, BindingFlags flags)
        where T : Delegate
    {
        var p = _tUtil.GetProperty(prop, flags);
        if (p == null) return null;
        var m = p.GetSetMethod(true);
        return m != null ? (T)(object)Delegate.CreateDelegate(typeof(T), m) : null;
    }
}
