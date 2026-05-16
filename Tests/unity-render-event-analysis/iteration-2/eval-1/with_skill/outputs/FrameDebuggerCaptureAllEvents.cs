using System;
using System.IO;
using System.Text;
using ShaderVariantCollector.UTSVC;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// Unity Editor test script that captures all FrameDebugger draw-call events
/// and exports shaderName, passName, lightMode, passIndex, shaderKeywords to CSV.
///
/// Dependency: FrameDebuggerReflect (namespace ShaderVariantCollector.UTSVC) —
///             an internal-API reflection wrapper around FrameDebuggerUtility.
///
/// Usage:
///   1. Open the Unity Editor with a Game View active.
///   2. Click menu: Tools > Frame Debugger > Capture All Events
///   3. Wait for the capture to complete (progress is logged to console).
///   4. Open Assets/FrameDebuggerOutput/events.csv to inspect results.
/// </summary>
public static class FrameDebuggerCaptureAllEvents
{
    private const string OutputPath = "Assets/FrameDebuggerOutput/events.csv";

    /// <summary>CSV buffer (null = waiting for Phase 1 event population).</summary>
    private static StringBuilder _csv;

    /// <summary>Next event index to capture.</summary>
    private static int _targetEventIndex;

    /// <summary>Total number of events in the current frame.</summary>
    private static int _eventCount;

    /// <summary>Number of events where GetFrameEventData returned true.</summary>
    private static int _captured;

    // =========================================================================
    // Entry Point
    // =========================================================================

    [MenuItem("Tools/Frame Debugger/Capture All Events")]
    public static void StartCapture()
    {
        if (!FrameDebuggerReflect.IsAvailable)
        {
            Debug.LogError("[FDCapture] FrameDebuggerReflect not available. " +
                           "Reflection initialization may have failed.");
            return;
        }

        // Reset state
        _csv = null;
        _targetEventIndex = 0;
        _eventCount = 0;
        _captured = 0;

        // Re-enable FrameDebugger for a clean capture state
        int remoteGUID = FrameDebuggerReflect.GetRemotePlayerGUID();
        FrameDebuggerReflect.SetEnabled(false, remoteGUID);
        FrameDebuggerReflect.SetEnabled(true, remoteGUID);

        // Hook into editor update loop
        EditorApplication.update -= CaptureTick;
        EditorApplication.update += CaptureTick;

        Debug.Log("[FDCapture] FrameDebugger enabled. Waiting for events to populate...");
    }

    // =========================================================================
    // Capture State Machine (runs via EditorApplication.update)
    // =========================================================================

    private static void CaptureTick()
    {
        // ---------- Phase 1: Wait for events to populate ----------
        if (_csv == null)
        {
            Array events = FrameDebuggerReflect.GetFrameEvents();
            _eventCount = events != null && events.Length > 0
                ? events.Length
                : FrameDebuggerReflect.GetCount();

            if (_eventCount <= 0)
            {
                // Force repaint to trigger FrameDebugger event population
                InternalEditorUtility.RepaintAllViews();
                return;
            }

            Debug.Log($"[FDCapture] Found {_eventCount} events. Beginning capture...");

            // Initialize CSV with header row
            _csv = new StringBuilder();
            _csv.AppendLine("index,shaderName,passName,lightMode,passIndex,shaderKeywords,hasData");

            // Set limit to 1 to populate data for the first event
            FrameDebuggerReflect.SetLimit(1);
            return;
        }

        // ---------- Phase 2: Capture event at limit - 1 ----------
        int curIndex = FrameDebuggerReflect.GetLimit() - 1;

        object data = FrameDebuggerReflect.CreateEventData();
        if (data != null)
        {
            bool hasData = FrameDebuggerReflect.GetFrameEventData(curIndex, data);

            // Append CSV row
            _csv.Append(curIndex).Append(',');
            _csv.Append(Escape(FrameDebuggerReflect.GetOriginalShaderName(data))).Append(',');
            _csv.Append(Escape(FrameDebuggerReflect.GetPassName(data))).Append(',');
            _csv.Append(Escape(FrameDebuggerReflect.GetPassLightMode(data))).Append(',');
            _csv.Append(FrameDebuggerReflect.GetShaderPassIndex(data)).Append(',');
            _csv.Append(Escape(FrameDebuggerReflect.GetShaderKeywords(data))).Append(',');
            _csv.AppendLine(hasData ? "1" : "0");

            if (hasData)
            {
                _captured++;
            }
        }

        _targetEventIndex++;

        // ---------- Phase 3: Check for completion ----------
        if (_targetEventIndex >= _eventCount)
        {
            FinishCapture();
            return;
        }

        // Advance limit to next event
        FrameDebuggerReflect.SetLimit(_targetEventIndex + 1);
    }

    // =========================================================================
    // Finish: disable FD, write CSV, log summary
    // =========================================================================

    private static void FinishCapture()
    {
        // Remove update hook
        EditorApplication.update -= CaptureTick;

        // Disable FrameDebugger
        FrameDebuggerReflect.SetEnabled(false, FrameDebuggerReflect.GetRemotePlayerGUID());

        // Ensure output directory exists
        string fullPath = Path.GetFullPath(
            Path.Combine(Application.dataPath, "../", OutputPath));

        string directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write CSV file
        File.WriteAllText(fullPath, _csv.ToString(), Encoding.UTF8);

        // Refresh AssetDatabase so the file appears in the Project window
        AssetDatabase.Refresh();

        Debug.Log($"[FDCapture] Capture complete. " +
                  $"Total events: {_eventCount}, " +
                  $"Drawcalls (hasData=true): {_captured}. " +
                  $"Output -> {OutputPath}");
    }

    // =========================================================================
    // CSV Helpers
    // =========================================================================

    /// <summary>
    /// Escapes a string value for CSV: wraps in double-quotes if the value
    /// contains commas, quotes, or newlines. Internal quotes are doubled.
    /// </summary>
    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
