using System;
using System.IO;
using System.Text;
using ShaderVariantCollector.UTSVC;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// Captures all draw call events from the Frame Debugger and exports them
/// as a CSV file (Assets/FrameDebuggerOutput/events.csv).
///
/// Fields exported per event: shaderName, passName, lightMode, passIndex, shaderKeywords.
///
/// Usage: Tools -> Frame Debugger -> Capture All Events
/// </summary>
public static class FrameDebuggerCaptureTest
{
    private const string OutputPath = "Assets/FrameDebuggerOutput/events.csv";

    private static int _totalEventCount;
    private static int _currentCaptureIndex;
    private static StringBuilder _csv;
    private static int _capturedCount;

    [MenuItem("Tools/Frame Debugger/Capture All Events")]
    public static void CaptureAllEvents()
    {
        if (!FrameDebuggerReflect.IsAvailable)
        {
            Debug.LogError("[FrameDebuggerCaptureTest] FrameDebuggerReflect is not available. " +
                           "Are you in a supported Unity version?");
            return;
        }

        // Reset state
        _csv = null;
        _currentCaptureIndex = 0;
        _totalEventCount = 0;
        _capturedCount = 0;

        // Ensure FrameDebugger is enabled
        int guid = FrameDebuggerReflect.GetRemotePlayerGUID();
        FrameDebuggerReflect.SetEnabled(false, guid);
        FrameDebuggerReflect.SetEnabled(true, guid);

        // Start the capture loop
        EditorApplication.update -= CaptureLoopTick;
        EditorApplication.update += CaptureLoopTick;

        Debug.Log("[FrameDebuggerCaptureTest] FrameDebugger enabled. Starting capture...");
    }

    private static void CaptureLoopTick()
    {
        // --- Phase 1: Initialize ---
        if (_csv == null)
        {
            var events = FrameDebuggerReflect.GetFrameEvents();
            _totalEventCount = events != null && events.Length > 0
                ? events.Length
                : FrameDebuggerReflect.GetCount();

            if (_totalEventCount <= 0)
            {
                // Not ready yet; force repaint to let the debugger catch up
                InternalEditorUtility.RepaintAllViews();
                return;
            }

            Debug.Log($"[FrameDebuggerCaptureTest] Found {_totalEventCount} frame events. Sweeping...");

            _csv = new StringBuilder();
            _csv.AppendLine("index,shaderName,passName,lightMode,passIndex,shaderKeywords");
            _capturedCount = 0;

            // Step to the first event
            FrameDebuggerReflect.SetLimit(1);
            return;
        }

        // --- Phase 2: Capture the current event ---
        CaptureCurrentEvent();

        // --- Phase 3: Advance or finish ---
        _currentCaptureIndex++;

        if (_currentCaptureIndex >= _totalEventCount)
        {
            FinishCapture();
            return;
        }

        FrameDebuggerReflect.SetLimit(_currentCaptureIndex + 1);
    }

    private static void CaptureCurrentEvent()
    {
        int currentLimitIndex = FrameDebuggerReflect.GetLimit() - 1;
        if (currentLimitIndex < 0)
            return;

        object data = FrameDebuggerReflect.CreateEventData();
        if (data == null)
        {
            Debug.LogError($"[FrameDebuggerCaptureTest] Failed to create FrameDebuggerEventData at index {_currentCaptureIndex}.");
            return;
        }

        bool hasData = FrameDebuggerReflect.GetFrameEventData(currentLimitIndex, data);
        if (!hasData)
            return;

        string shaderName = FrameDebuggerReflect.GetOriginalShaderName(data);
        string realShaderName = FrameDebuggerReflect.GetRealShaderName(data);
        string passName = FrameDebuggerReflect.GetPassName(data);
        string lightMode = FrameDebuggerReflect.GetPassLightMode(data);
        int passIndex = FrameDebuggerReflect.GetShaderPassIndex(data);
        string shaderKeywords = FrameDebuggerReflect.GetShaderKeywords(data);

        // Use realShaderName as fallback if original is empty
        string effectiveShaderName = !string.IsNullOrEmpty(shaderName) ? shaderName : realShaderName;

        AppendCsvRow(_currentCaptureIndex, effectiveShaderName, passName, lightMode, passIndex, shaderKeywords);
        _capturedCount++;

        if (_capturedCount <= 30)
        {
            Debug.Log($"[FrameDebuggerCaptureTest] [#{_currentCaptureIndex}] shader='{effectiveShaderName}', " +
                      $"pass='{passName}', lightMode='{lightMode}', passIndex={passIndex}, " +
                      $"keywords='{shaderKeywords}'");
        }
    }

    private static void AppendCsvRow(int index, string shaderName, string passName,
        string lightMode, int passIndex, string shaderKeywords)
    {
        _csv.Append(index).Append(',');
        _csv.Append(CsvEscape(shaderName)).Append(',');
        _csv.Append(CsvEscape(passName)).Append(',');
        _csv.Append(CsvEscape(lightMode)).Append(',');
        _csv.Append(passIndex).Append(',');
        _csv.AppendLine(CsvEscape(shaderKeywords));
    }

    private static void FinishCapture()
    {
        // Remove update hook
        EditorApplication.update -= CaptureLoopTick;

        // Disable FrameDebugger
        int guid = FrameDebuggerReflect.GetRemotePlayerGUID();
        FrameDebuggerReflect.SetEnabled(false, guid);

        // Write CSV file
        string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", OutputPath));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            File.WriteAllText(fullPath, _csv.ToString(), Encoding.UTF8);
            AssetDatabase.Refresh();
            Debug.Log($"[FrameDebuggerCaptureTest] Capture complete. " +
                      $"Exported {_capturedCount} events to {OutputPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[FrameDebuggerCaptureTest] Failed to write CSV: {ex.Message}");
        }
    }

    /// <summary>
    /// Escapes a string value for CSV: wraps in double-quotes if it contains
    /// commas, double-quotes, or newlines. Embedded double-quotes are doubled.
    /// </summary>
    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
