# FrameDebuggerReflect — Reflection Wrapper Template

This is the complete pattern for wrapping FrameDebuggerUtility internal APIs via reflection.
Copy this as a starting point, then customize the data extraction fields as needed.

## Data Class

```csharp
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Raw event data extracted from FrameDebuggerEventData via reflection.
/// </summary>
public class FrameDebuggerRawEvent
{
    public int index = -1;
    public string name;
    public Shader shader;
    public string shaderName;
    public string shaderPath;
    public string shaderGuid;
    public int passIndex = -1;
    public string passName;
    public string lightMode;
    public string shaderKeywords;
}
```

## Reflection Wrapper

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

public static class FrameDebuggerReflect
{
    // --- Resolved types ---
    private static Type _tUtil;       // FrameDebuggerUtility
    private static Type _tEventData;  // FrameDebuggerEventData

    // --- Delegate-backed API access (fast) ---
    private static Action<bool, int> _setEnabled;
    private static Func<int> _getRemoteGUID;
    private static Func<Array> _getFrameEvents;
    private static Func<int, string> _getEventInfoName;
    private static Func<int, UnityEngine.Object> _getEventObject;
    private static Func<int> _getCount;
    private static Func<int> _getLimit;
    private static Action<int> _setLimit;

    // --- MethodInfo for GetFrameEventData (complex param type) ---
    private static MethodInfo _miGetFrameEventData;

    // --- Event data field accessors ---
    private static FieldInfo _fiOriginalShaderName;
    private static FieldInfo _fiRealShaderName;
    private static FieldInfo _fiPassName;
    private static FieldInfo _fiPassLightMode;
    private static FieldInfo _fiShaderInstanceID;
    private static FieldInfo _fiShaderPassIndex;
    private static FieldInfo _fiShaderKeywords;
    private static FieldInfo _fiSubShaderIndex;
    // Add more fields as needed: _fiMesh, _fiVertexCount, etc.

    public static bool IsAvailable => _tUtil != null && _tEventData != null;

    static FrameDebuggerReflect()
    {
        try
        {
            var editorAsm = typeof(Editor).Assembly;
            _tUtil = editorAsm.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
            _tEventData = editorAsm.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData");

            if (_tUtil == null || _tEventData == null)
            {
                Debug.LogError("[FrameDebuggerReflect] Types not found.");
                return;
            }

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            // Static method delegates
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

            // Property delegates
            _getCount = CreatePropertyGetter<Func<int>>("count", flags);
            _getLimit = CreatePropertyGetter<Func<int>>("limit", flags);
            _setLimit = CreatePropertySetter<Action<int>>("limit", flags);

            // GetFrameEventData: complex param (FrameDebuggerEventData type at runtime)
            _miGetFrameEventData = _tUtil.GetMethod(
                "GetFrameEventData", flags, null,
                new[] { typeof(int), _tEventData }, null);

            // Event data instance fields
            var fi = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _fiOriginalShaderName = _tEventData.GetField("m_OriginalShaderName", fi);
            _fiRealShaderName = _tEventData.GetField("m_RealShaderName", fi);
            _fiPassName = _tEventData.GetField("m_PassName", fi);
            _fiPassLightMode = _tEventData.GetField("m_PassLightMode", fi);
            _fiShaderInstanceID = _tEventData.GetField("m_ShaderInstanceID", fi);
            _fiShaderPassIndex = _tEventData.GetField("m_ShaderPassIndex", fi);
            _fiShaderKeywords = _tEventData.GetField("shaderKeywords", fi);
            _fiSubShaderIndex = _tEventData.GetField("m_SubShaderIndex", fi);

            Debug.Log("[FrameDebuggerReflect] Initialized.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[FrameDebuggerReflect] Init failed: {e.Message}");
        }
    }

    // --- Public API ---

    public static void SetEnabled(bool enable, int guid) => _setEnabled?.Invoke(enable, guid);
    public static int GetRemotePlayerGUID() => _getRemoteGUID != null ? _getRemoteGUID() : 0;
    public static int GetCount() => _getCount != null ? _getCount() : 0;
    public static int GetLimit() => _getLimit != null ? _getLimit() : 0;
    public static void SetLimit(int value) { if (_setLimit != null) _setLimit(value); }
    public static Array GetFrameEvents() => _getFrameEvents?.Invoke();
    public static string GetFrameEventInfoName(int index) =>
        _getEventInfoName != null ? _getEventInfoName(index) : string.Empty;
    public static UnityEngine.Object GetFrameEventObject(int index) =>
        _getEventObject?.Invoke(index);

    public static bool GetFrameEventData(int index, object data)
    {
        if (_miGetFrameEventData == null || data == null) return false;
        return (bool)_miGetFrameEventData.Invoke(null, new[] { index, data });
    }

    public static object CreateEventData()
    {
        try { return Activator.CreateInstance(_tEventData, true); }
        catch { return null; }
    }

    // --- Data field accessors ---
    public static string GetOriginalShaderName(object data) =>
        _fiOriginalShaderName?.GetValue(data) as string ?? string.Empty;
    public static string GetRealShaderName(object data) =>
        _fiRealShaderName?.GetValue(data) as string ?? string.Empty;
    public static string GetPassName(object data) =>
        _fiPassName?.GetValue(data) as string ?? string.Empty;
    public static string GetPassLightMode(object data) =>
        _fiPassLightMode?.GetValue(data) as string ?? string.Empty;
    public static int GetShaderInstanceID(object data) =>
        (int)(_fiShaderInstanceID?.GetValue(data) ?? 0);
    public static int GetShaderPassIndex(object data) =>
        (int)(_fiShaderPassIndex?.GetValue(data) ?? -1);
    public static string GetShaderKeywords(object data) =>
        _fiShaderKeywords?.GetValue(data) as string ?? string.Empty;
    public static int GetSubShaderIndex(object data) =>
        (int)(_fiSubShaderIndex?.GetValue(data) ?? 0);

    // --- Async Capture ---

    /// <summary>
    /// Start an async capture of all frame events via EditorApplication.update.
    /// Calls onDone with the captured events when complete.
    /// </summary>
    public static void StartCapture(Action<List<FrameDebuggerRawEvent>> onDone)
    {
        var result = new List<FrameDebuggerRawEvent>();
        int targetIndex = 0;
        int eventCount = 0;

        void Tick()
        {
            // Phase 1: Wait for events to populate
            if (eventCount <= 0)
            {
                var events = GetFrameEvents();
                eventCount = events != null && events.Length > 0
                    ? events.Length : GetCount();
                if (eventCount <= 0)
                {
                    InternalEditorUtility.RepaintAllViews();
                    return;
                }

                SetLimit(1);  // Start with first event
                return;
            }

            // Phase 2: Capture current event
            int curIndex = GetLimit() - 1;
            var raw = CaptureRawEvent(curIndex);
            if (raw != null) result.Add(raw);
            targetIndex++;

            // Phase 3: Done or continue
            if (targetIndex >= eventCount)
            {
                EditorApplication.update -= Tick;
                onDone?.Invoke(result);
                return;
            }

            SetLimit(targetIndex + 1);
        }

        EditorApplication.update += Tick;
    }

    private static FrameDebuggerRawEvent CaptureRawEvent(int index)
    {
        object data = CreateEventData();
        if (data == null) return null;

        bool hasData = GetFrameEventData(index, data);
        if (!hasData) return null;

        var shader = EditorUtility.InstanceIDToObject(
            GetShaderInstanceID(data)) as Shader;
        var raw = new FrameDebuggerRawEvent
        {
            index = index,
            name = GetFrameEventInfoName(index),
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

    // --- Reflection helpers ---

    private static T CreateStaticDelegate<T>(
        string methodName, BindingFlags flags, Type[] paramTypes = null)
        where T : Delegate
    {
        var method = paramTypes != null
            ? _tUtil.GetMethod(methodName, flags, null, paramTypes, null)
            : _tUtil.GetMethod(methodName, flags);
        if (method == null)
        {
            Debug.LogWarning($"[FrameDebuggerReflect] Method '{methodName}' not found.");
            return null;
        }
        return (T)(object)Delegate.CreateDelegate(typeof(T), method);
    }

    private static T CreatePropertyGetter<T>(string propName, BindingFlags flags)
        where T : Delegate
    {
        var prop = _tUtil.GetProperty(propName, flags);
        if (prop == null) return null;
        var method = prop.GetGetMethod(true);
        return method != null
            ? (T)(object)Delegate.CreateDelegate(typeof(T), method) : null;
    }

    private static T CreatePropertySetter<T>(string propName, BindingFlags flags)
        where T : Delegate
    {
        var prop = _tUtil.GetProperty(propName, flags);
        if (prop == null) return null;
        var method = prop.GetSetMethod(true);
        return method != null
            ? (T)(object)Delegate.CreateDelegate(typeof(T), method) : null;
    }
}
```

## Key Decisions and Why

| Decision | Why |
|---|---|
| `Delegate.CreateDelegate` for static methods | ~10x faster than `MethodInfo.Invoke`, close to direct call speed |
| `MethodInfo.Invoke` for `GetFrameEventData` | Method takes `FrameDebuggerEventData` (internal type) as parameter — `Delegate` would need the exact type at compile time |
| `Activator.CreateInstance(type, true)` | Internal classes have internal constructors; `true` bypasses visibility checks |
| Static constructor initialization | Types resolved once — if reflection fails, it fails early and loudly |
| Null-unsafe public accessors | If init fails, all delegates are null; `?.Invoke` prevents crashes but returns default values |
