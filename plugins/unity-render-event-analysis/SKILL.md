---
name: unity-render-event-analysis
description: >
  Use this skill when the user needs to analyze what shaders, passes, and keyword variants are being rendered in the Unity Editor.
  This covers capturing per-frame draw-call information, extracting shader/pass/keyword data from rendering events, parsing shader
  pass metadata via ShaderUtil.GetShaderData, building custom shader variant collectors, or analyzing variant usage across scenes.
  Trigger on keywords like "drawcall analysis", "draw call", "shader variant collection", "pass analysis", "FrameDebugger",
  "frame debugger", "capture draw calls", "collect shader variants", "shader pass metadata", "ShaderUtil.GetShaderData",
  "GetFrameEventData", or "GetFrameEvents". Also trigger when the user needs to avoid depending on Unity's internal API
  assemblies (InternalAPIEditorBridge). Chinese triggers: "抓取drawcall", "收集渲染信息", "渲染分析", "变体收集",
  "变体分析", "反射抓取渲染数据", "逐帧抓取".
description_zh: >
  分析 Unity Editor 逐帧渲染指令数据。通过反射封装 FrameDebuggerUtility 捕获每帧所有 drawcall 的 shader/pass/keyword 信息，
  使用 ShaderUtil.GetShaderData 提取 pass 元数据，构建自定义 shader variant 收集器。不依赖 InternalAPIEditorBridge。
---

# Unity Render Event Analysis — Capture and Inspect Per-Frame Rendering Data

## Why This Exists

Unity's `FrameDebuggerUtility` (in `UnityEditorInternal`) is an **internal API** — not documented, not guaranteed across versions, and
requiring reflection or `InternalAPIEditorBridge` assemblies to access. The reflection approach makes your code portable across Unity
versions and removes the dependency on fragile bridge assemblies.

This skill teaches how to:

1. **Reflect into FrameDebuggerUtility** — access internal APIs via delegate-based reflection
2. **Capture all draw-call events** for the current frame
3. **Extract shader/pass/keyword data** from each event
4. **Parse shader pass metadata** via `ShaderUtil.GetShaderData`
5. **Combine everything** into a pass-aware shader variant collector

## The FrameDebugger Capture Pattern

The core insight is that `FrameDebuggerUtility.limit` controls which event's data is populated for `GetFrameEventData`. Without setting
`limit` per-event, you only get data for the last event. The capture flow is:

```
Enable FrameDebugger  →  Wait for events (RepaintAllViews + editor frames)
→  For each event i:  Set limit = i+1, Wait 1 frame, GetFrameEventData(i, data), Extract shader/pass/keywords
→  Disable FrameDebugger
```

### Key APIs (Internal — accessed via reflection)

| Method/Property | Purpose |
|---|---|
| `FrameDebuggerUtility.SetEnabled(bool, int)` | Enable/disable with remote player GUID |
| `FrameDebuggerUtility.GetRemotePlayerGUID()` | Returns -1 for local player |
| `FrameDebuggerUtility.GetFrameEvents()` | Returns FrameDebuggerEvent[] for current frame |
| `FrameDebuggerUtility.count` | Total number of captured frame events |
| `FrameDebuggerUtility.limit` | **Critical**: controls which event is "selected" for data population |
| `FrameDebuggerUtility.GetFrameEventData(int, FrameDebuggerEventData)` | Populates data object for event at limit-1 |
| `FrameDebuggerUtility.GetFrameEventInfoName(int)` | Event display name (works without limit) |
| `FrameDebuggerUtility.GetFrameEventObject(int)` | UnityEngine.Object for the event (works without limit) |

### FrameDebuggerEventData Fields (Internal)

| Field | Content |
|---|---|
| `m_OriginalShaderName` | Original shader name assigned to the material |
| `m_RealShaderName` | Actual shader used (may differ due to rendering passes) |
| `m_PassName` | Pass name from the shader |
| `m_PassLightMode` | LightMode tag value |
| `m_ShaderInstanceID` | Instance ID of the Shader object (use `EditorUtility.InstanceIDToObject`) |
| `m_SubShaderIndex` | Which subshader is active |
| `m_ShaderPassIndex` | Global pass index across all subshaders |
| `shaderKeywords` | Material keywords as space-separated string |
| `m_Mesh` | Mesh being rendered |
| `m_VertexCount` / `m_IndexCount` / `m_InstanceCount` / `m_DrawCallCount` | Rendering stats |
| `m_ShaderInfo` | Nested object with detailed shader info (keywords, floats, textures, etc.) |

### Nested ShaderInfo Keywords

`m_ShaderInfo.m_Keywords[]` is an array of keyword structs with:
- `m_Name` — keyword string
- `m_Flags` — keyword flags
- `m_IsGlobal` — whether it's a global keyword
- `m_IsDynamic` — whether it's a dynamic keyword

## Step-by-Step: Writing the Code

### Step 1: Reflection Wrapper Class

Create a static class (e.g. `FrameDebuggerReflect`) that resolves all internal types and members once in its static constructor.

The internal types to resolve:
```
UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility (static utility class)
UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData (event data class)
```

See `references/reflection-wrapper.md` for the complete template. Key patterns:

- Use `typeof(Editor).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility")` to find the type
- Use `Delegate.CreateDelegate` for static methods and property getters/setters — ~10x faster than `MethodInfo.Invoke`
- Use `MethodInfo.Invoke` for `GetFrameEventData` — its parameter type (`FrameDebuggerEventData`) is only resolvable at runtime
- Use `Activator.CreateInstance(type, true)` to create instances of internal types (the `true` bypasses constructor visibility)
- **Always null-check delegates**; log once and degrade gracefully — don't crash the Editor if reflection fails

### Step 2: The Capture Loop

Use `EditorApplication.update` as the tick mechanism. The loop state machine:

1. **Phase 1 — Wait for events**: After enabling FrameDebugger, call `InternalEditorUtility.RepaintAllViews()` until
   `GetFrameEvents()` returns non-null or `count > 0`.

2. **Phase 2 — Iterate events**: Set `limit = targetIndex + 1`, wait one editor frame, then call
   `GetFrameEventData(targetIndex, data)` and extract fields. Repeat for all events.

3. **Phase 3 — Done**: Remove update handler, disable FrameDebugger, call completion callback.

See `references/capture-loop.md` for the complete pattern.

### Step 3: Extract Shader Pass Metadata

Use `ShaderUtil.GetShaderData(shader)` (public Editor API — no reflection needed) to get structured pass information:

```
ShaderData → SubshaderCount → GetSubshader(i) → PassCount → GetPass(j) → pass.Name
```

The global `passIndex` is the flattened index across all subshaders:
```csharp
int passIndex = 0;
for (int sub = 0; sub < shaderData.SubshaderCount; sub++)
{
    var subShader = shaderData.GetSubshader(sub);
    for (int local = 0; local < subShader.PassCount; local++)
    {
        var pass = subShader.GetPass(local);
        // passIndex = the global index; local = the per-subshader index
        passIndex++;
    }
}
```

To extract the LightMode tag:
`pass.GetType().GetMethod("FindTagValue").Invoke(pass, new[] { new ShaderTagId("LightMode") })`.

See `references/shader-metadata.md` for the complete `ShaderPassExtractor` with `PassType` guessing and `FindBestMatch` fallback.

### Step 4: Combine Into a Variant Collector

Merge FrameDebugger capture results with ShaderUtil pass metadata. The key decision is pass identity resolution:

**Priority chain** (when matching a captured draw-call to a shader pass):
1. **By passIndex** if available (`>= 0`) — use `ShaderPassExtractor.FindByIndex(shader, passIndex)`, exact match
2. **By passName** — fallback when passIndex is -1
3. **By lightMode** — fallback when passName is also missing
4. **By PassType** — last resort fallback

Then normalize keywords (sort, deduplicate) for stable comparison, and deduplicate using a composite key:
```
shader|passIndex|keywords|passName|lightMode
```

> **Common mistake**: Using only `(shader, passType, keywords)` like Unity's built-in SVC. This collapses variants from different passes with the same PassType, defeating the purpose of pass-aware collection. Always include passIndex and passName.

See `references/recorder.md` for the complete record data model, merge logic, and keyword normalization utilities.

## Important Caveats

- **`limit` IS mandatory**: `GetFrameEventData` only returns valid data for the event at `limit - 1`. You must set limit per-event.
- **One frame wait**: After changing `limit`, you need one EditorApplication.update tick before `GetFrameEventData` returns true.
- **FrameDebugger re-enable**: If FrameDebugger was already enabled, disable then re-enable for a clean capture state.
- **Event count 0**: If `GetFrameEvents()` returns null/empty, call `RepaintAllViews()` to trigger event population.
- **No batch mode**: FrameDebugger requires an active Editor GameView. It does not work in `-batchmode`.
- **Performance**: Each limit change + 1 frame wait = N frames for N events. Complex scenes with 500+ events will take 500+ frames.

## PassType Guessing (when passIndex is -1)

When FrameDebugger cannot determine the passIndex, guess `PassType` from passName or lightMode:

```csharp
static PassType GuessPassType(string passName, string lightMode)
{
    var value = !string.IsNullOrEmpty(lightMode) ? lightMode : passName;
    if (string.IsNullOrEmpty(value)) return PassType.Normal;

    if (value.IndexOf("ShadowCaster", OrdinalIgnoreCase) >= 0
        || value.IndexOf("Caster", OrdinalIgnoreCase) >= 0)
        return PassType.ShadowCaster;
    if (value.IndexOf("Meta", OrdinalIgnoreCase) >= 0)
        return PassType.Meta;
    if (value.IndexOf("Deferred", OrdinalIgnoreCase) >= 0)
        return PassType.Deferred;
    if (value.IndexOf("ForwardBase", OrdinalIgnoreCase) >= 0)
        return PassType.ForwardBase;
    if (value.IndexOf("ForwardAdd", OrdinalIgnoreCase) >= 0)
        return PassType.ForwardAdd;
    if (value.IndexOf("Motion", OrdinalIgnoreCase) >= 0)
        return PassType.MotionVectors;

    return PassType.Normal;
}
```

Without this logic, ShadowCaster/Meta/Deferred variants get assigned to `PassType.Normal`, causing the stripper to retain
wrong variants. This is the #1 gap that eval runs showed as easy to miss — **always include it**.

## Composite Deduplication Key

FrameDebugger can capture the same shader variant multiple times. Use a composite key to deduplicate:

```csharp
string dedupKey = $"{shader.name}|{passIndex}|{keywordKey}|{passName}|{lightMode}";
```

Why all 5 fields:
- `shader` — different shaders have different pass layouts
- `passIndex` — same PassType can appear in multiple passes (e.g., two `PassType.Normal`)
- `keywords` — same pass with different keyword combos are different variants
- `passName` — FrameDebugger may report passIndex=-1; passName disambiguates
- `lightMode` — extra disambiguation when both passIndex and passName are unreliable

## Reference Files

- `references/reflection-wrapper.md` — Complete FrameDebuggerReflect template with all field accessors and StartCapture
- `references/capture-loop.md` — Test script showing the full capture loop pattern
- `references/shader-metadata.md` — Shader pass metadata extractor with PassType guessing and FindBestMatch fallback
- `references/recorder.md` — Complete variant recorder combining FD capture + pass metadata, with dedup and keyword normalization
