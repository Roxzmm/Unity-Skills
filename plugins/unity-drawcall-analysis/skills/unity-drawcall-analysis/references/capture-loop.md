# Frame Debugger Capture Loop — Test Script Template

This demonstrates the complete capture loop: enable FrameDebugger, iterate all events,
extract data, and save to CSV. Use this as a test/verification script.

```csharp
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

public static class FrameDebuggerCaptureTest
{
    private const string OutputPath = "Assets/FrameDebuggerOutput/events.csv";

    private static StringBuilder _csv;
    private static int _targetEventIndex;
    private static int _eventCount;
    private static int _captured;

    [MenuItem("Tools/Frame Debugger/Capture All Events")]
    public static void StartCapture()
    {
        if (!FrameDebuggerReflect.IsAvailable)
        {
            Debug.LogError("[Test] FrameDebuggerReflect not available.");
            return;
        }

        // Enable FrameDebugger (disable first for clean state)
        int id = FrameDebuggerReflect.GetRemotePlayerGUID();
        FrameDebuggerReflect.SetEnabled(false, id);
        FrameDebuggerReflect.SetEnabled(true, id);

        _csv = null;
        _targetEventIndex = 0;
        _captured = 0;

        EditorApplication.update -= CaptureTick;
        EditorApplication.update += CaptureTick;

        Debug.Log("[Test] FrameDebugger enabled, waiting for events...");
    }

    private static void CaptureTick()
    {
        // Phase 1: Wait for events to populate
        if (_csv == null)
        {
            var events = FrameDebuggerReflect.GetFrameEvents();
            _eventCount = events != null && events.Length > 0
                ? events.Length
                : FrameDebuggerReflect.GetCount();

            if (_eventCount <= 0)
            {
                // Force repaint to trigger event population
                InternalEditorUtility.RepaintAllViews();
                return;
            }

            Debug.Log($"[Test] Found {_eventCount} events. Starting capture...");
            _csv = new StringBuilder();
            _csv.AppendLine(
                "index,eventName,shaderName,passName,lightMode," +
                "passIndex,subShaderIndex,shaderKeywords,hasData");

            // Set limit to 1 to populate data for the first event
            FrameDebuggerReflect.SetLimit(1);
            return;
        }

        // Phase 2: Capture event at current limit-1
        int curIndex = FrameDebuggerReflect.GetLimit() - 1;

        object data = FrameDebuggerReflect.CreateEventData();
        if (data != null)
        {
            bool hasData = FrameDebuggerReflect.GetFrameEventData(curIndex, data);
            string eventName = SafeGetEventName(curIndex);

            _csv.Append(curIndex).Append(',');
            _csv.Append(Escape(eventName)).Append(',');
            _csv.Append(Escape(FrameDebuggerReflect.GetOriginalShaderName(data))).Append(',');
            _csv.Append(Escape(FrameDebuggerReflect.GetPassName(data))).Append(',');
            _csv.Append(Escape(FrameDebuggerReflect.GetPassLightMode(data))).Append(',');
            _csv.Append(FrameDebuggerReflect.GetShaderPassIndex(data)).Append(',');
            _csv.Append(FrameDebuggerReflect.GetSubShaderIndex(data)).Append(',');
            _csv.Append(Escape(FrameDebuggerReflect.GetShaderKeywords(data))).Append(',');
            _csv.AppendLine(hasData ? "1" : "0");

            if (hasData)
            {
                _captured++;
                Debug.Log($"[Test] #{curIndex}: " +
                    $"shader='{FrameDebuggerReflect.GetOriginalShaderName(data)}', " +
                    $"pass='{FrameDebuggerReflect.GetPassName(data)}', " +
                    $"lightMode='{FrameDebuggerReflect.GetPassLightMode(data)}'");
            }
        }

        _targetEventIndex++;

        // Phase 3: Done or advance to next event
        if (_targetEventIndex >= _eventCount)
        {
            FinishCapture();
            return;
        }

        FrameDebuggerReflect.SetLimit(_targetEventIndex + 1);
    }

    private static void FinishCapture()
    {
        EditorApplication.update -= CaptureTick;
        FrameDebuggerReflect.SetEnabled(false,
            FrameDebuggerReflect.GetRemotePlayerGUID());

        string fullPath = Path.GetFullPath(
            Path.Combine(Application.dataPath, "../", OutputPath));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        File.WriteAllText(fullPath, _csv.ToString(), Encoding.UTF8);
        AssetDatabase.Refresh();

        Debug.Log($"[Test] Done. Captured {_captured} shader events -> {OutputPath}");
    }

    private static string SafeGetEventName(int index)
    {
        try { return FrameDebuggerReflect.GetFrameEventInfoName(index); }
        catch { return string.Empty; }
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}";
    }
}
```

## Notes

- **StartCapture replaces Tick**: The reflection-wrapper.md includes a `StartCapture(Action<List<...>>)` method that
  wraps this loop pattern. Use it when you need the results as a List in memory rather than writing to CSV.
- **Frame delay**: Each event iteration takes 1 editor frame. A scene with 300 events takes ~5 seconds at 60fps.
- **Debug logs**: The first 30 or so captured events are logged for quick inspection during development.
- **Shader info**: `GetFrameEventData` returns false for some events (non-rendering events like "Clear"). Skip those.
