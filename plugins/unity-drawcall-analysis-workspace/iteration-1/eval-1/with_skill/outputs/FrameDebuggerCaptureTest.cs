using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// Editor test script that captures all FrameDebugger draw-call events and exports
/// shader/pass/keyword data to a CSV file.
///
/// Usage: Tools -> Frame Debugger -> Capture All Events
/// Output: Assets/FrameDebuggerOutput/events.csv
///
/// Requires: FrameDebuggerReflect utility class (reflection wrapper around
/// internal UnityEditorInternal.FrameDebuggerInternal APIs).
/// </summary>
public static class FrameDebuggerCaptureTest
{
    private const string OutputPath = "Assets/FrameDebuggerOutput/events.csv";

    /// <summary>
    /// State machine phase for the EditorApplication.update-driven capture loop.
    /// </summary>
    private enum Phase
    {
        /// <summary>Waiting for events to populate after enabling FrameDebugger.</summary>
        WaitingForEvents,
        /// <summary>Iterating through events one-by-one.</summary>
        Capturing,
        /// <summary>Capture complete or error — cleanup pending.</summary>
        Done,
    }

    // --- Capture state ---
    private static Phase _phase;
    private static StringBuilder _csv;
    private static int _eventCount;

    /// <summary>Index of the next event to capture (0-based).</summary>
    private static int _targetIndex;

    /// <summary>Number of events where GetFrameEventData returned true.</summary>
    private static int _capturedWithData;

    /// <summary>Number of events skipped (GetFrameEventData returned false).</summary>
    private static int _capturedEmpty;

    // ======================================================================
    // Menu entry
    // ======================================================================

    [MenuItem("Tools/Frame Debugger/Capture All Events")]
    public static void StartCapture()
    {
        if (!FrameDebuggerReflect.IsAvailable)
        {
            Debug.LogError(
                "[FrameDebuggerCaptureTest] FrameDebuggerReflect not available. " +
                "Reflection init may have failed. Check the Console for earlier errors.");
            EditorUtility.DisplayDialog(
                "FrameDebugger Capture",
                "FrameDebuggerReflect is not available. Check the Console for errors.",
                "OK");
            return;
        }

        // Disable first to ensure a clean capture state (SKILL.md caveat).
        int remoteGuid = FrameDebuggerReflect.GetRemotePlayerGUID();
        FrameDebuggerReflect.SetEnabled(false, remoteGuid);
        FrameDebuggerReflect.SetEnabled(true, remoteGuid);

        // Reset state
        _phase = Phase.WaitingForEvents;
        _csv = null;
        _eventCount = 0;
        _targetIndex = 0;
        _capturedWithData = 0;
        _capturedEmpty = 0;

        // Remove stale delegate before adding (defensive against double-call).
        EditorApplication.update -= CaptureTick;
        EditorApplication.update += CaptureTick;

        Debug.Log("[FrameDebuggerCaptureTest] FrameDebugger enabled, " +
                  "waiting for events to populate...");
    }

    // ======================================================================
    // EditorApplication.update tick
    // ======================================================================

    private static void CaptureTick()
    {
        try
        {
            switch (_phase)
            {
                case Phase.WaitingForEvents:
                    TickWaitForEvents();
                    break;
                case Phase.Capturing:
                    TickCaptureEvent();
                    break;
                case Phase.Done:
                    // Safety: remove ourselves if somehow still scheduled.
                    Cleanup();
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"[FrameDebuggerCaptureTest] Unhandled exception in capture loop: {ex}");
            Cleanup();
        }
    }

    // ======================================================================
    // Phase 1 — Wait for events to become available
    // ======================================================================

    private static void TickWaitForEvents()
    {
        var frameEvents = FrameDebuggerReflect.GetFrameEvents();
        _eventCount = frameEvents != null && frameEvents.Length > 0
            ? frameEvents.Length
            : FrameDebuggerReflect.GetCount();

        if (_eventCount <= 0)
        {
            // Force repaint so the internal event list gets populated.
            InternalEditorUtility.RepaintAllViews();
            return;
        }

        Debug.Log($"[FrameDebuggerCaptureTest] Found {_eventCount} events. " +
                  "Starting capture...");

        // Initialise CSV with BOM-less UTF-8 header.
        _csv = new StringBuilder();
        _csv.AppendLine(
            "Index,EventName,ShaderName,PassName,LightMode," +
            "PassIndex,SubShaderIndex,ShaderKeywords,HasData");

        // Set limit to 1 (populates data for event at index 0).
        FrameDebuggerReflect.SetLimit(1);

        _phase = Phase.Capturing;
    }

    // ======================================================================
    // Phase 2 — Capture one event per frame tick
    // ======================================================================

    private static void TickCaptureEvent()
    {
        // The currently selected event is at (limit - 1).
        int curIndex = FrameDebuggerReflect.GetLimit() - 1;

        // Create a fresh FrameDebuggerEventData instance via reflection.
        object data = FrameDebuggerReflect.CreateEventData();

        if (data != null)
        {
            bool hasData = FrameDebuggerReflect.GetFrameEventData(curIndex, data);

            string eventName = SafeGetEventName(curIndex);
            string shaderName = FrameDebuggerReflect.GetOriginalShaderName(data);
            string passName = FrameDebuggerReflect.GetPassName(data);
            string lightMode = FrameDebuggerReflect.GetPassLightMode(data);
            int passIndex = FrameDebuggerReflect.GetShaderPassIndex(data);
            int subShaderIndex = FrameDebuggerReflect.GetSubShaderIndex(data);
            string shaderKeywords = FrameDebuggerReflect.GetShaderKeywords(data);

            // Append CSV row (with proper escaping).
            _csv.Append(curIndex).Append(',');
            _csv.Append(CsvEscape(eventName)).Append(',');
            _csv.Append(CsvEscape(shaderName)).Append(',');
            _csv.Append(CsvEscape(passName)).Append(',');
            _csv.Append(CsvEscape(lightMode)).Append(',');
            _csv.Append(passIndex).Append(',');
            _csv.Append(subShaderIndex).Append(',');
            _csv.Append(CsvEscape(shaderKeywords)).Append(',');
            _csv.AppendLine(hasData ? "1" : "0");

            if (hasData)
            {
                _capturedWithData++;
                Debug.Log($"[FrameDebuggerCaptureTest] #{curIndex}: " +
                    $"shader='{shaderName}', pass='{passName}', " +
                    $"lightMode='{lightMode}', passIndex={passIndex}");
            }
            else
            {
                _capturedEmpty++;
            }
        }
        else
        {
            // Could not create data object — write a minimal placeholder row.
            _csv.Append(curIndex).Append(',');
            _csv.Append(CsvEscape(SafeGetEventName(curIndex))).Append(',');
            _csv.AppendLine(",,,,,0");
            _capturedEmpty++;
        }

        _targetIndex++;

        // Phase 3 — Finished or advance to next event.
        if (_targetIndex >= _eventCount)
        {
            FinishCapture();
            return;
        }

        // Set limit to the next event. This causes GetFrameEventData to
        // populate the data for that event on the next frame tick.
        FrameDebuggerReflect.SetLimit(_targetIndex + 1);
    }

    // ======================================================================
    // Phase 3 — Cleanup and write CSV
    // ======================================================================

    private static void FinishCapture()
    {
        string fullPath = Path.GetFullPath(OutputPath);

        try
        {
            // Ensure the output directory exists.
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Write CSV with UTF-8 encoding (no BOM).
            File.WriteAllText(fullPath, _csv?.ToString() ?? string.Empty, Encoding.UTF8);

            // Tell Unity to re-scan the asset database so the file appears.
            AssetDatabase.Refresh();

            Debug.Log(
                $"[FrameDebuggerCaptureTest] Capture complete. " +
                $"{_capturedWithData} events with data + {_capturedEmpty} empty events " +
                $"written to {OutputPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"[FrameDebuggerCaptureTest] Failed to write CSV to {fullPath}: {ex.Message}");
        }
        finally
        {
            _phase = Phase.Done;
            Cleanup();
        }
    }

    /// <summary>
    /// Removes the update delegate and disables the FrameDebugger.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    private static void Cleanup()
    {
        EditorApplication.update -= CaptureTick;

        try
        {
            FrameDebuggerReflect.SetEnabled(false,
                FrameDebuggerReflect.GetRemotePlayerGUID());
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[FrameDebuggerCaptureTest] Cleanup warning: {ex.Message}");
        }

        _phase = Phase.Done;
    }

    // ======================================================================
    // Helpers
    // ======================================================================

    /// <summary>
    /// Returns the event display name, or an empty string if the call fails
    /// (non-draw-call events or internal errors).
    /// </summary>
    private static string SafeGetEventName(int index)
    {
        try
        {
            return FrameDebuggerReflect.GetFrameEventInfoName(index) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Properly escapes a value for CSV.
    ///
    /// Rules (RFC 4180):
    ///   - If the value contains a comma, double-quote, or newline, wrap in double quotes.
    ///   - Any double-quote characters inside the value are doubled ("").
    /// </summary>
    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // Fast path: no special characters.
        if (value.IndexOfAny(SpecialCsvChars) < 0)
            return value;

        // Slow path: escape quotes, then wrap.
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    /// <summary>
    /// Characters that require CSV quoting.
    /// </summary>
    private static readonly char[] SpecialCsvChars = { ',', '"', '\n', '\r' };
}
