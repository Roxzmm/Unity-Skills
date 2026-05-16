#if UNITY_EDITOR

using UnityEditor;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// Unity Editor tool that captures all FrameDebugger drawcall events and exports them to CSV.
///
/// Usage:
///   Click "Tools/Frame Debugger/Capture All Events" in the Unity Editor menu.
///   The tool will enable the FrameDebugger, wait for frames to be captured,
///   then extract every drawcall event's shaderName, passName, lightMode,
///   passIndex, and shaderKeywords, saving the result to:
///     Assets/FrameDebuggerOutput/events.csv
///
/// Design:
///   - All access to FrameDebuggerUtility and FrameDebuggerEventData is done
///     via reflection -- no dependency on InternalAPIEditorBridge or any
///     non-public API assembly reference.
///   - Works across Unity 2020-2023+ by probing multiple possible namespaces,
///     method names, and field names at runtime.
///   - Uses EditorApplication.update polling (not coroutines) since this is
///     editor code that must work outside of Play Mode.
/// </summary>
public static class FrameDebuggerCaptureTool
{
    /* ==================================================================
     * Reflected API cache -- resolved once in InitializeReflection()
     * ================================================================*/

    // --- FrameDebuggerUtility (internal, lives in UnityEngine.dll) ---
    private static Type    s_FrameDebuggerUtilityType;
    private static MethodInfo s_GetFrameEventCountMethod;
    private static MethodInfo s_GetFrameEventDataMethod;
    private static Type    s_EventDataType;       // FrameDebuggerEventData (internal struct)
    private static MethodInfo s_LimitFrameCountMethod;

    // --- Fields on the event-data struct (names vary across Unity versions) ---
    private static FieldInfo s_ShaderNameField;
    private static FieldInfo s_PassNameField;
    private static FieldInfo s_PassIndexField;
    private static FieldInfo s_ShaderKeywordsField;
    private static FieldInfo s_LightModeField;

    // --- JumpToFrame (may or may not be public depending on version) ---
    private static MethodInfo s_JumpToFrameMethod;
    private static MethodInfo s_SetFrameIndexMethod;   // fallback on FrameDebuggerUtility

    /* ==================================================================
     * Capture state
     * ================================================================*/

    private static bool   s_IsCapturing;
    private static string s_OutputPath;
    private static int    s_TargetFrameCount;
    private static double s_PollStartTime;
    private static int    s_LastFrameCount;
    private static int    s_StablePolls;

    private const int    DEFAULT_FRAME_LIMIT = 100;
    private const int    STABLE_POLLS_BEFORE_PROCESS = 5;
    private const double POLL_TIMEOUT_SECONDS = 30.0;

    /* ==================================================================
     * Menu entry
     * ================================================================*/

    [MenuItem("Tools/Frame Debugger/Capture All Events")]
    private static void CaptureAllEvents()
    {
        if (s_IsCapturing)
        {
            Debug.LogWarning("[FrameDebuggerCapture] A capture is already in progress.");
            return;
        }

        if (!InitializeReflection())
            return;

        // Setup output file -------------------------------------------------
        // Unity convention: "Assets/..." maps to Application.dataPath.
        string outputDir = Path.Combine(Application.dataPath, "FrameDebuggerOutput");
        Directory.CreateDirectory(outputDir);
        s_OutputPath = Path.Combine(outputDir, "events.csv");

        // Delete previous CSV file if it already exists.
        if (File.Exists(s_OutputPath))
            File.Delete(s_OutputPath);

        // Start the FrameDebugger -------------------------------------------
        try
        {
            FrameDebugger.enabled = true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[FrameDebuggerCapture] Failed to enable FrameDebugger: {ex.Message}");
            return;
        }

        s_TargetFrameCount = DEFAULT_FRAME_LIMIT;
        SetFrameLimit(s_TargetFrameCount);

        // Begin polling -----------------------------------------------------
        s_IsCapturing    = true;
        s_PollStartTime  = EditorApplication.timeSinceStartup;
        s_LastFrameCount = -1;
        s_StablePolls    = 0;
        EditorApplication.update += OnCapturePoll;

        Debug.Log($"[FrameDebuggerCapture] FrameDebugger enabled. Capturing up to {s_TargetFrameCount} frames...");
    }

    /* ==================================================================
     * Polling callback -- called every editor tick while capturing
     * ================================================================*/

    private static void OnCapturePoll()
    {
        if (!FrameDebugger.enabled)
        {
            // FrameDebugger was disabled externally (e.g. user toggled it off).
            CleanupPoll("FrameDebugger was disabled externally. " +
                        "Process any frames already captured.");
            return;
        }

        int currentFrameCount = FrameDebugger.frameCount;

        // Stability detection: if frame count hasn't changed for several polls
        // we assume capture is complete (or stuck).
        if (currentFrameCount == s_LastFrameCount)
        {
            s_StablePolls++;

            bool reachedTarget  = currentFrameCount >= s_TargetFrameCount;
            bool stableEnough   = s_StablePolls >= STABLE_POLLS_BEFORE_PROCESS;

            if (reachedTarget || stableEnough)
            {
                CleanupPoll(null);
                return;
            }
        }
        else
        {
            s_LastFrameCount = currentFrameCount;
            s_StablePolls    = 0;
        }

        // Timeout guard
        if (EditorApplication.timeSinceStartup - s_PollStartTime > POLL_TIMEOUT_SECONDS)
        {
            Debug.LogWarning($"[FrameDebuggerCapture] Poll timed out after {POLL_TIMEOUT_SECONDS}s " +
                             $"with {currentFrameCount} frame(s) captured.");
            CleanupPoll(null);
        }
    }

    /// <summary>Stop polling, run the extraction, then disable FrameDebugger.</summary>
    private static void CleanupPoll(string warningMessage)
    {
        EditorApplication.update -= OnCapturePoll;

        if (!string.IsNullOrEmpty(warningMessage))
            Debug.LogWarning($"[FrameDebuggerCapture] {warningMessage}");

        try
        {
            ProcessAllFrames();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[FrameDebuggerCapture] Error while processing events: {ex}");
        }
        finally
        {
            if (FrameDebugger.enabled)
                FrameDebugger.enabled = false;
            s_IsCapturing = false;
        }
    }

    /* ==================================================================
     * Core extraction logic
     * ================================================================*/

    private static void ProcessAllFrames()
    {
        int totalFrameCount = FrameDebugger.frameCount;

        if (totalFrameCount < 1)
        {
            Debug.LogWarning("[FrameDebuggerCapture] No frames were captured. Nothing to export.");
            return;
        }

        Debug.Log($"[FrameDebuggerCapture] Processing {totalFrameCount} frame(s)...");

        int totalEvents = 0;

        using (var writer = new StreamWriter(s_OutputPath, false, Encoding.UTF8))
        {
            // CSV header
            writer.WriteLine("FrameIndex,EventIndex,ShaderName,PassName,LightMode,PassIndex,ShaderKeywords");

            for (int frameIndex = 0; frameIndex < totalFrameCount; frameIndex++)
            {
                // Jump to the specific frame so GetFrameEventCount/Data
                // return data for this frame.
                JumpToFrame(frameIndex);

                int eventCount = (int)s_GetFrameEventCountMethod.Invoke(null, null);

                for (int eventIndex = 0; eventIndex < eventCount; eventIndex++)
                {
                    // Fetch event data via reflection
                    object eventData = s_GetFrameEventDataMethod.Invoke(
                        null, new object[] { eventIndex });

                    // Extract all five fields ----------------------------------
                    string shaderName     = GetFieldString(eventData, s_ShaderNameField);
                    string passName       = GetFieldString(eventData, s_PassNameField);
                    string lightMode      = GetFieldString(eventData, s_LightModeField);
                    string passIndexStr   = GetFieldString(eventData, s_PassIndexField);
                    string shaderKeywords = GetKeywordsString(eventData);

                    // Write CSV row --------------------------------------------
                    writer.WriteLine(
                        $"{frameIndex}," +
                        $"{eventIndex}," +
                        $"{EscapeCsv(shaderName)}," +
                        $"{EscapeCsv(passName)}," +
                        $"{EscapeCsv(lightMode)}," +
                        $"{passIndexStr}," +
                        $"{EscapeCsv(shaderKeywords)}"
                    );

                    totalEvents++;
                }
            }
        }

        Debug.Log($"[FrameDebuggerCapture] Done. {totalEvents} events exported to:\n  {s_OutputPath}");

        // Ping the asset so the user can quickly open it in the Editor.
        string relativePath = "Assets/FrameDebuggerOutput/events.csv";
        var asset = AssetDatabase.LoadMainAssetAtPath(relativePath);
        if (asset != null)
        {
            EditorGUIUtility.PingObject(asset);
        }
        else
        {
            AssetDatabase.Refresh();
            asset = AssetDatabase.LoadMainAssetAtPath(relativePath);
            if (asset != null)
                EditorGUIUtility.PingObject(asset);
        }
    }

    /* ==================================================================
     * Reflection initialization
     * ================================================================*/

    private static bool InitializeReflection()
    {
        // -------- 1. Locate FrameDebuggerUtility type ----------------
        // Historically in UnityEngine.Experimental.Rendering; moved to
        // UnityEngine.Rendering in later versions.
        // It lives in UnityEngine.dll (not UnityEditor.dll).
        Assembly engineAssembly = typeof(UnityEngine.Object).Assembly;

        s_FrameDebuggerUtilityType =
            engineAssembly.GetType("UnityEngine.Experimental.Rendering.FrameDebuggerUtility")
            ?? engineAssembly.GetType("UnityEngine.Rendering.FrameDebuggerUtility");

        // If still not found, search all loaded assemblies (paranoid fallback).
        if (s_FrameDebuggerUtilityType == null)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                s_FrameDebuggerUtilityType =
                    asm.GetType("UnityEngine.Experimental.Rendering.FrameDebuggerUtility")
                    ?? asm.GetType("UnityEngine.Rendering.FrameDebuggerUtility");
                if (s_FrameDebuggerUtilityType != null)
                    break;
            }
        }

        if (s_FrameDebuggerUtilityType == null)
        {
            Debug.LogError("[FrameDebuggerCapture] Could not locate FrameDebuggerUtility type. " +
                           "This Unity version's FrameDebugger internal API may have changed.");
            return false;
        }

        // -------- 2. Resolve methods on FrameDebuggerUtility ----------
        const BindingFlags kStaticAnyAccess =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        s_GetFrameEventCountMethod = s_FrameDebuggerUtilityType.GetMethod(
            "GetFrameEventCount", kStaticAnyAccess);

        s_GetFrameEventDataMethod = s_FrameDebuggerUtilityType.GetMethod(
            "GetFrameEventData", kStaticAnyAccess,
            null, new Type[] { typeof(int) }, null);

        if (s_GetFrameEventCountMethod == null)
        {
            Debug.LogError("[FrameDebuggerCapture] FrameDebuggerUtility.GetFrameEventCount() not found.");
            return false;
        }
        if (s_GetFrameEventDataMethod == null)
        {
            Debug.LogError("[FrameDebuggerCapture] FrameDebuggerUtility.GetFrameEventData(int) not found.");
            return false;
        }

        // -------- 3. Determine the event-data struct type -------------
        s_EventDataType = s_GetFrameEventDataMethod.ReturnType;
        if (s_EventDataType == null || s_EventDataType == typeof(void))
        {
            Debug.LogError("[FrameDebuggerCapture] GetFrameEventData has void return type.");
            return false;
        }

        // -------- 4. Find fields on the event-data struct -------------
        const BindingFlags kInstanceAnyAccess =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Probe multiple possible field names in case the Unity version
        // uses m_ prefix or a different naming convention.
        s_ShaderNameField   = FindField(s_EventDataType, kInstanceAnyAccess,
                                         "shaderName", "m_ShaderName");
        s_PassNameField     = FindField(s_EventDataType, kInstanceAnyAccess,
                                         "passName", "m_PassName");
        s_PassIndexField    = FindField(s_EventDataType, kInstanceAnyAccess,
                                         "passIndex", "m_PassIndex");
        s_ShaderKeywordsField = FindField(s_EventDataType, kInstanceAnyAccess,
                                          "shaderKeywords", "m_ShaderKeywords",
                                          "keywords", "m_Keywords");
        s_LightModeField    = FindField(s_EventDataType, kInstanceAnyAccess,
                                         "lightMode", "m_LightMode",
                                         "lightmode", "m_Lightmode");

        // shaderName is mandatory; everything else is best-effort.
        if (s_ShaderNameField == null)
        {
            Debug.LogError("[FrameDebuggerCapture] Cannot find 'shaderName' field on " +
                           s_EventDataType.FullName);
            return false;
        }

        // -------- 5. Resolve JumpToFrame (may be public vs internal) ---
        // Try public API first
        s_JumpToFrameMethod = typeof(FrameDebugger).GetMethod(
            "JumpToFrame", BindingFlags.Public | BindingFlags.Static,
            null, new Type[] { typeof(int) }, null);

        // Fall back to non-public
        if (s_JumpToFrameMethod == null)
        {
            s_JumpToFrameMethod = typeof(FrameDebugger).GetMethod(
                "JumpToFrame", BindingFlags.NonPublic | BindingFlags.Static,
                null, new Type[] { typeof(int) }, null);
        }

        // If JumpToFrame doesn't exist at all, try SetFrameIndex on
        // FrameDebuggerUtility (older API shape).
        if (s_JumpToFrameMethod == null)
        {
            s_SetFrameIndexMethod = s_FrameDebuggerUtilityType.GetMethod(
                "SetFrameIndex", kStaticAnyAccess,
                null, new Type[] { typeof(int) }, null);

            if (s_SetFrameIndexMethod == null)
            {
                Debug.LogError("[FrameDebuggerCapture] Neither FrameDebugger.JumpToFrame(int) " +
                               "nor FrameDebuggerUtility.SetFrameIndex(int) could be resolved. " +
                               "Cannot iterate frames.");
                return false;
            }
        }

        // -------- 6. Resolve LimitFrameCount ---------------------------
        s_LimitFrameCountMethod = s_FrameDebuggerUtilityType.GetMethod(
            "LimitFrameCount", kStaticAnyAccess);

        return true;
    }

    /// <summary>Try each candidate field name until one is found.</summary>
    private static FieldInfo FindField(Type type, BindingFlags flags, params string[] candidates)
    {
        foreach (string name in candidates)
        {
            var fi = type.GetField(name, flags);
            if (fi != null)
                return fi;
        }
        return null;
    }

    /* ==================================================================
     * Helper: set the frame-capture limit
     * ================================================================*/

    private static void SetFrameLimit(int count)
    {
        // In many Unity versions limitFrameCount is a public property on
        // FrameDebugger.  Try that first.
        try
        {
            FrameDebugger.limitFrameCount = count;
            return;
        }
        catch
        {
            // Not accessible as public -- continue below.
        }

        // Try via reflection on FrameDebugger (non-public property).
        try
        {
            var prop = typeof(FrameDebugger).GetProperty(
                "limitFrameCount",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(null, count);
                return;
            }
        }
        catch
        {
            // ignore
        }

        // Fall back to FrameDebuggerUtility.LimitFrameCount(int).
        if (s_LimitFrameCountMethod != null)
        {
            s_LimitFrameCountMethod.Invoke(null, new object[] { count });
        }
    }

    /* ==================================================================
     * Helper: jump to a specific captured frame
     * ================================================================*/

    private static void JumpToFrame(int frameIndex)
    {
        if (s_JumpToFrameMethod != null)
        {
            s_JumpToFrameMethod.Invoke(null, new object[] { frameIndex });
        }
        else if (s_SetFrameIndexMethod != null)
        {
            s_SetFrameIndexMethod.Invoke(null, new object[] { frameIndex });
        }
        // If both are null we would have failed in InitializeReflection.
    }

    /* ==================================================================
     * Helper: safely read a field as a string
     * ================================================================*/

    private static string GetFieldString(object obj, FieldInfo field)
    {
        if (field == null) return "";
        try
        {
            object val = field.GetValue(obj);
            return val?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    /* ==================================================================
     * Helper: convert shaderKeywords field (typically string[]) to a
     * semicolon-delimited string.
     * ================================================================*/

    private static string GetKeywordsString(object eventData)
    {
        if (s_ShaderKeywordsField == null)
            return "";

        object val;
        try { val = s_ShaderKeywordsField.GetValue(eventData); }
        catch { return ""; }

        if (val == null)
            return "";

        // The field is usually a string[], but could theoretically be
        // a List<string> -- handle both generically.
        var parts = new List<string>();

        if (val is Array arr)
        {
            foreach (object item in arr)
            {
                if (item != null)
                    parts.Add(item.ToString());
            }
        }
        else if (val is System.Collections.IList list)
        {
            foreach (object item in list)
            {
                if (item != null)
                    parts.Add(item.ToString());
            }
        }
        else
        {
            return val.ToString();
        }

        return string.Join(";", parts.ToArray());
    }

    /* ==================================================================
     * Helper: CSV field escaping (RFC 4180 compatible)
     * ================================================================*/

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        // If the value contains a comma, double-quote, or newline, wrap
        // the entire value in double-quotes and double any embedded quotes.
        if (value.IndexOf(',') >= 0 ||
            value.IndexOf('"') >= 0 ||
            value.IndexOf('\n') >= 0 ||
            value.IndexOf('\r') >= 0)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}

#endif // UNITY_EDITOR
