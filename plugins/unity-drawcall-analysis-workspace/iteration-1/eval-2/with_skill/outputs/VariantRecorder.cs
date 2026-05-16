#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace UTDrawcallAnalysis
{
    // ========================================================================
    // Data Models
    // ========================================================================

    /// <summary>
    /// A single recorded shader variant, combining FrameDebugger drawcall info
    /// with pass metadata from ShaderUtil.GetShaderData.
    /// </summary>
    [Serializable]
    public class ShaderVariantData
    {
        /// <summary>Name of the Shader (e.g. "Universal Render Pipeline/Lit").</summary>
        public string shaderName;

        /// <summary>Pass index as reported by FrameDebugger (matched to local or global index).</summary>
        public int passIndex;

        /// <summary>Human-readable pass name extracted from ShaderData.</summary>
        public string passName;

        /// <summary>"LightMode" tag of the matched pass (e.g. "UniversalForward", "ShadowCaster").</summary>
        public string lightMode;

        /// <summary>PassType of the matched pass (e.g. ScriptableRenderPipeline, ShadowCaster, Normal).</summary>
        public PassType passType;

        /// <summary>Sorted, deduplicated list of shader keywords active for this variant.</summary>
        public string[] keywords;

        public ShaderVariantData Clone()
        {
            return new ShaderVariantData
            {
                shaderName = shaderName,
                passIndex = passIndex,
                passName = passName,
                lightMode = lightMode,
                passType = passType,
                keywords = keywords?.ToArray(),
            };
        }
    }

    /// <summary>
    /// Top-level container for JSON serialisation of a variant collection.
    /// </summary>
    [Serializable]
    public class ShaderVariantCollectionData
    {
        public List<ShaderVariantData> variants;
        public string captureTimestamp;
        public int totalDrawcalls;
        public int uniqueVariants;

        public ShaderVariantCollectionData()
        {
            variants = new List<ShaderVariantData>();
        }
    }

    // ========================================================================
    // VariantRecorder
    // ========================================================================

    /// <summary>
    /// Captures shader variants by merging FrameDebugger drawcall data with
    /// ShaderUtil.GetShaderData pass metadata.
    /// <para>
    /// <b>Workflow:</b><br/>
    /// 1. Call <see cref="BeginCapture"/> (or use the menu item).<br/>
    /// 2. Play the scene (or let the Game/Scene view render one frame).<br/>
    /// 3. The snapshot is processed automatically and saved to a JSON file.<br/>
    /// </para>
    /// <para>
    /// Each output record contains (shaderName, passIndex, passName, lightMode,
    /// passType, normalisedKeywords), deduplicated by (shader, passIndex, keywords)
    /// and sorted lexicographically.
    /// </para>
    /// <para>
    /// <b>Note:</b> All files must be placed in an Editor folder.
    /// </para>
    /// </summary>
    public static class VariantRecorder
    {
        // -- capture state ---------------------------------------------------
        private static string s_outputPath;
        private static List<ShaderVariantData> s_capturedVariants;
        private static int s_totalDrawcalls;
        private static bool s_isCapturing;
        private static bool s_previousEnabledState;
        private static DateTime s_startTime;

        // -- reflection cache ------------------------------------------------
        private static Type s_frameDebuggerType;
        private static Type s_eventDataType;
        private static PropertyInfo s_enabledProp;
        private static PropertyInfo s_eventCountProp;
        private static MethodInfo s_getEventDataMethod;
        private static EventInfo s_takeSnapshotEvent;
        private static Delegate s_takeSnapshotHandler;

        // fields on FrameDebuggerEventData
        private static FieldInfo s_eventDataShaderField;
        private static FieldInfo s_eventDataMaterialField;
        private static FieldInfo s_eventDataPassIndexField;
        private static FieldInfo s_eventDataKeywordsField;

        // -- pass-metadata cache: shaderName -> (passIndex -> ShaderPassInfo) -
        private static readonly Dictionary<string, Dictionary<int, ShaderPassInfo>> s_passCache =
            new Dictionary<string, Dictionary<int, ShaderPassInfo>>();

        // ====================================================================
        // Initialisation
        // ====================================================================

        static VariantRecorder()
        {
            InitReflection();
        }

        private static void InitReflection()
        {
            try
            {
                s_frameDebuggerType = typeof(FrameDebugger);

                s_enabledProp = s_frameDebuggerType.GetProperty("enabled",
                    BindingFlags.Public | BindingFlags.Static);
                s_eventCountProp = s_frameDebuggerType.GetProperty("eventCount",
                    BindingFlags.Public | BindingFlags.Static);
                s_getEventDataMethod = s_frameDebuggerType.GetMethod("GetFrameEventData",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(int) }, null);
                s_takeSnapshotEvent = s_frameDebuggerType.GetEvent("takeSnapshot",
                    BindingFlags.Public | BindingFlags.Static);

                // Determine the FrameDebuggerEventData type
                if (s_getEventDataMethod != null)
                {
                    s_eventDataType = s_getEventDataMethod.ReturnType;
                }
                else
                {
                    // Fallback: resolve by name
                    s_eventDataType = Type.GetType(
                        "UnityEngine.Rendering.FrameDebuggerEventData, UnityEngine.CoreModule");
                }

                if (s_eventDataType != null)
                {
                    const BindingFlags instPub = BindingFlags.Public | BindingFlags.Instance;
                    s_eventDataShaderField   = s_eventDataType.GetField("shader",   instPub);
                    s_eventDataMaterialField = s_eventDataType.GetField("material", instPub);
                    s_eventDataPassIndexField = s_eventDataType.GetField("passIndex", instPub);
                    s_eventDataKeywordsField  = FindKeywordsField(s_eventDataType);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VariantRecorder] Reflection init failed: {ex.Message}");
            }
        }

        private static FieldInfo FindKeywordsField(Type t)
        {
            string[] candidates = { "shaderKeywords", "m_ShaderKeywords", "keywords", "m_Keywords" };
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var name in candidates)
            {
                var f = t.GetField(name, flags);
                if (f != null && IsKeywordCompatible(f.FieldType))
                    return f;
            }
            return null;
        }

        private static bool IsKeywordCompatible(Type ft)
        {
            if (ft == typeof(string[])) return true;
            if (ft.IsArray)
            {
                var elem = ft.GetElementType();
                return elem != null && (elem == typeof(string) || elem.Name == "ShaderKeyword");
            }
            return false;
        }

        // ====================================================================
        // Public API
        // ====================================================================

        /// <summary>
        /// Start a FrameDebugger capture.  Subscribe to the snapshot event,
        /// enable the debugger, and wait for the next rendered frame.
        /// Results are written to <paramref name="outputPath"/> as JSON.
        /// </summary>
        /// <param name="outputPath">Full file path for the output .json file.</param>
        public static void BeginCapture(string outputPath)
        {
            if (s_isCapturing)
            {
                Debug.LogWarning("[VariantRecorder] A capture is already in progress. Cancel it first.");
                return;
            }

            if (string.IsNullOrEmpty(outputPath))
            {
                Debug.LogError("[VariantRecorder] Output path is empty.");
                return;
            }

            // Ensure the target directory exists
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            s_outputPath = outputPath;
            s_capturedVariants = new List<ShaderVariantData>();
            s_totalDrawcalls = 0;
            s_isCapturing = true;
            s_startTime = DateTime.Now;

            // Clear stale pass cache
            s_passCache.Clear();

            // Subscribe to FrameDebugger.takeSnapshot
            bool subscribed = false;
            if (s_takeSnapshotEvent != null)
            {
                try
                {
                    var mi = typeof(VariantRecorder).GetMethod("OnTakeSnapshot",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (mi != null)
                    {
                        s_takeSnapshotHandler = Delegate.CreateDelegate(s_takeSnapshotEvent.EventHandlerType, mi);
                        s_takeSnapshotEvent.AddEventHandler(null, s_takeSnapshotHandler);
                        subscribed = true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VariantRecorder] Could not subscribe to takeSnapshot: {ex.Message}");
                }
            }

            if (!subscribed)
            {
                // Fallback: poll via EditorApplication.update
                EditorApplication.update += PollFrameDebugger;
                Debug.Log("[VariantRecorder] Using polling fallback for FrameDebugger.");
            }

            // Remember previous state so we can restore it later
            s_previousEnabledState = IsFrameDebuggerEnabled();
            SetFrameDebuggerEnabled(true);

            Debug.Log($"[VariantRecorder] Capture started. Output: {s_outputPath}");
            Debug.Log("Play the scene or interact with the Game/Scene view to capture a frame.");
        }

        /// <summary>
        /// Cancel an in-progress capture and restore the FrameDebugger state.
        /// </summary>
        public static void CancelCapture()
        {
            if (!s_isCapturing) return;
            CleanupCapture();
            Debug.Log("[VariantRecorder] Capture cancelled.");
        }

        // ====================================================================
        // FrameDebugger callbacks
        // ====================================================================

        private static void OnTakeSnapshot()
        {
            if (!s_isCapturing) return;

            Debug.Log($"[VariantRecorder] Frame snapshot received after " +
                      $"{(DateTime.Now - s_startTime).TotalSeconds:F1}s. Processing...");

            ProcessCapturedFrame();
            SaveResults();
            CleanupCapture();
        }

        private static void PollFrameDebugger()
        {
            if (!s_isCapturing)
            {
                EditorApplication.update -= PollFrameDebugger;
                return;
            }

            // Timeout after 30 seconds
            if ((DateTime.Now - s_startTime).TotalSeconds > 30)
            {
                Debug.LogWarning("[VariantRecorder] Capture timed out (30 s). No frame was captured.");
                EditorApplication.update -= PollFrameDebugger;
                CleanupCapture();
                return;
            }

            if (IsFrameDebuggerEnabled() && GetEventCount() > 0)
            {
                EditorApplication.update -= PollFrameDebugger;
                Debug.Log($"[VariantRecorder] Captured {GetEventCount()} events via polling.");
                ProcessCapturedFrame();
                SaveResults();
                CleanupCapture();
            }
        }

        // ====================================================================
        // Event processing
        // ====================================================================

        private static void ProcessCapturedFrame()
        {
            int eventCount = GetEventCount();
            s_totalDrawcalls = eventCount;

            Debug.Log($"[VariantRecorder] Processing {eventCount} events...");

            for (int i = 0; i < eventCount; i++)
            {
                try { ProcessSingleEvent(i); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VariantRecorder] Event {i} failed: {ex.Message}");
                }

                if (i > 0 && i % 100 == 0)
                {
                    EditorUtility.DisplayProgressBar(
                        "VariantRecorder",
                        $"Processing event {i} / {eventCount}",
                        (float)i / eventCount);
                }
            }

            EditorUtility.ClearProgressBar();
            Debug.Log($"[VariantRecorder] Done. {s_totalDrawcalls} drawcalls → {s_capturedVariants.Count} raw records.");
        }

        private static void ProcessSingleEvent(int index)
        {
            object eventData = GetEventData(index);
            if (eventData == null) return;

            Shader shader = ReadShader(eventData);
            if (shader == null) return;

            int passIndex = ReadPassIndex(eventData);
            if (passIndex < 0) return;

            string[] rawKeywords = ReadKeywords(eventData);
            string[] normalisedKeywords = KeywordUtils.NormalizeKeywords(rawKeywords);

            ShaderPassInfo passInfo = ResolvePass(shader, passIndex);

            s_capturedVariants.Add(new ShaderVariantData
            {
                shaderName = shader.name,
                passIndex = passIndex,
                passName = passInfo.passName,
                lightMode = passInfo.lightMode,
                passType = passInfo.passType,
                keywords = normalisedKeywords,
            });
        }

        // ====================================================================
        // Pass metadata matching
        // ====================================================================

        private static ShaderPassInfo ResolvePass(Shader shader, int passIndex)
        {
            string name = shader.name;

            // Build + cache pass lookup for this shader
            if (!s_passCache.ContainsKey(name))
            {
                s_passCache[name] = ShaderPassExtractor.BuildLocalPassLookup(shader);
            }

            var localLookup = s_passCache[name];

            // 1) Try matching passIndex as a local pass index
            if (localLookup.TryGetValue(passIndex, out var info))
                return info;

            // 2) Fallback: try matching as a global (flattened) pass index
            var globalLookup = ShaderPassExtractor.BuildGlobalPassLookup(shader);
            if (globalLookup.TryGetValue(passIndex, out var globalInfo))
                return globalInfo;

            // 3) No match — return a stub
            return new ShaderPassInfo
            {
                subShaderIndex = -1,
                localPassIndex = passIndex,
                globalPassIndex = passIndex,
                passName = $"Pass_{passIndex}",
                lightMode = string.Empty,
                passType = PassType.Normal,
            };
        }

        // ====================================================================
        // Deduplication & sorting
        // ====================================================================

        private static List<ShaderVariantData> DeduplicateAndSort(List<ShaderVariantData> raw)
        {
            var unique = new Dictionary<string, ShaderVariantData>(raw.Count);

            foreach (var v in raw)
            {
                string key = KeywordUtils.GetVariantKey(v.shaderName, v.passIndex, v.keywords);
                if (!unique.ContainsKey(key))
                    unique[key] = v.Clone();
            }

            return unique.Values
                .OrderBy(v => v.shaderName, StringComparer.Ordinal)
                .ThenBy(v => v.passIndex)
                .ThenBy(v => KeywordUtils.GetKeywordsKey(v.keywords), StringComparer.Ordinal)
                .ToList();
        }

        // ====================================================================
        // Saving
        // ====================================================================

        private static void SaveResults()
        {
            var unique = DeduplicateAndSort(s_capturedVariants);

            var collection = new ShaderVariantCollectionData
            {
                variants = unique,
                captureTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                totalDrawcalls = s_totalDrawcalls,
                uniqueVariants = unique.Count,
            };

            string json = JsonUtility.ToJson(collection, true);

            try
            {
                File.WriteAllText(s_outputPath, json, Encoding.UTF8);
                Debug.Log($"[VariantRecorder] Saved {unique.Count} unique variants → {s_outputPath}");
                Debug.Log($"[VariantRecorder] Stats: {s_totalDrawcalls} drawcalls | {unique.Count} unique variants.");
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VariantRecorder] Failed to write '{s_outputPath}': {ex.Message}");
            }
        }

        // ====================================================================
        // Cleanup
        // ====================================================================

        private static void CleanupCapture()
        {
            s_isCapturing = false;

            // Unsubscribe from takeSnapshot
            if (s_takeSnapshotEvent != null && s_takeSnapshotHandler != null)
            {
                try { s_takeSnapshotEvent.RemoveEventHandler(null, s_takeSnapshotHandler); }
                catch { /* best effort */ }
                s_takeSnapshotHandler = null;
            }

            EditorApplication.update -= PollFrameDebugger;

            // Restore previous FrameDebugger state
            SetFrameDebuggerEnabled(s_previousEnabledState);
        }

        // ====================================================================
        // Reflection helpers
        // ====================================================================

        private static void SetFrameDebuggerEnabled(bool on)
        {
            try
            {
                if (s_enabledProp != null)
                    s_enabledProp.SetValue(null, on);
                else
                    FrameDebugger.enabled = on;
            }
            catch { }
        }

        private static bool IsFrameDebuggerEnabled()
        {
            try
            {
                if (s_enabledProp != null)
                    return (bool)s_enabledProp.GetValue(null);
                return FrameDebugger.IsEnabled();
            }
            catch { return false; }
        }

        private static int GetEventCount()
        {
            try
            {
                if (s_eventCountProp != null)
                    return (int)s_eventCountProp.GetValue(null);
                return FrameDebugger.eventCount;
            }
            catch { return 0; }
        }

        private static object GetEventData(int index)
        {
            try
            {
                if (s_getEventDataMethod != null)
                    return s_getEventDataMethod.Invoke(null, new object[] { index });
            }
            catch { }
            return null;
        }

        // -- field readers on FrameDebuggerEventData -------------------------

        private static Shader ReadShader(object eventData)
        {
            if (eventData == null || s_eventDataShaderField == null) return null;
            try { return s_eventDataShaderField.GetValue(eventData) as Shader; }
            catch { return null; }
        }

        private static int ReadPassIndex(object eventData)
        {
            if (eventData == null || s_eventDataPassIndexField == null) return -1;
            try { return (int)s_eventDataPassIndexField.GetValue(eventData); }
            catch { return -1; }
        }

        private static Material ReadMaterial(object eventData)
        {
            if (eventData == null || s_eventDataMaterialField == null) return null;
            try { return s_eventDataMaterialField.GetValue(eventData) as Material; }
            catch { return null; }
        }

        private static string[] ReadKeywords(object eventData)
        {
            if (eventData == null) return Array.Empty<string>();

            // 1) Try the dedicated keywords field on the event data
            if (s_eventDataKeywordsField != null)
            {
                try
                {
                    var val = s_eventDataKeywordsField.GetValue(eventData);
                    if (val != null) return ConvertKeywordValue(val);
                }
                catch { }
            }

            // 2) Fallback: material.shaderKeywords
            var mat = ReadMaterial(eventData);
            if (mat != null)
            {
                try { return mat.shaderKeywords; }
                catch { }
            }

            return Array.Empty<string>();
        }

        private static string[] ConvertKeywordValue(object value)
        {
            if (value is string[] strs)
                return strs;

            if (value is Array arr)
            {
                var result = new string[arr.Length];
                for (int i = 0; i < arr.Length; i++)
                {
                    var elem = arr.GetValue(i);
                    if (elem is string s)
                        result[i] = s;
                    else if (elem != null)
                    {
                        // Assume ShaderKeyword with a .name property
                        try
                        {
                            var np = elem.GetType().GetProperty("name",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (np != null)
                                result[i] = np.GetValue(elem) as string ?? string.Empty;
                        }
                        catch { result[i] = string.Empty; }
                    }
                }
                return result;
            }

            return Array.Empty<string>();
        }

        // ====================================================================
        // Menu items
        // ====================================================================

        [MenuItem("Tools/Shader Variant Capture/Capture From FrameDebugger _%#D", false, 100)]
        private static void MenuCapture()
        {
            string defaultName = $"variant_capture_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            string defaultDir  = Path.Combine(Application.dataPath, "..", "ShaderVariants");

            if (!Directory.Exists(defaultDir))
                Directory.CreateDirectory(defaultDir);

            string path = EditorUtility.SaveFilePanel(
                "Save Shader Variant Collection",
                defaultDir,
                defaultName,
                "json");

            if (string.IsNullOrEmpty(path)) return;
            BeginCapture(path);
        }

        [MenuItem("Tools/Shader Variant Capture/Cancel Capture", false, 101)]
        private static void MenuCancelCapture() => CancelCapture();

        [MenuItem("Tools/Shader Variant Capture/Cancel Capture", true)]
        private static bool MenuCancelCaptureValidate() => s_isCapturing;
    }
}

#endif // UNITY_EDITOR
