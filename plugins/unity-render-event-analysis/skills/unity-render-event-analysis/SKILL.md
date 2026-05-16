---
name: unity-render-event-analysis
description: >
  Use this skill when the user needs to analyze what shaders, passes, and keyword variants are being rendered in the Unity Editor.
  This covers capturing per-frame draw-call information, extracting shader/pass/keyword data from rendering events, parsing shader
  pass metadata via ShaderUtil.GetShaderData, building custom shader variant collectors, or analyzing variant usage across scenes.
  Trigger on keywords like "drawcall analysis", "draw call", "抓取drawcall", "收集渲染信息", "渲染分析", "shader variant collection",
  "变体收集", "变体分析", "pass analysis", "FrameDebugger", "frame debugger", "capture draw calls", "collect shader variants",
  "反射抓取渲染数据", "shader pass metadata", "ShaderUtil.GetShaderData", "逐帧抓取", "GetFrameEventData", or "GetFrameEvents".
  Also trigger when the user needs to avoid depending on Unity's internal API assemblies (InternalAPIEditorBridge).
description_zh: >
  分析 Unity Editor 逐帧渲染指令数据。通过反射封装 FrameDebuggerUtility 捕获每帧所有 drawcall 的 shader/pass/keyword 信息，
  使用 ShaderUtil.GetShaderData 提取 pass 元数据，构建自定义 shader variant 收集器。不依赖 InternalAPIEditorBridge。
---

# Unity Render Event Analysis — Capture and Inspect Per-Frame Rendering Data

> **English** — Primary documentation. Chinese (中文) explanations follow each section.

## Why This Exists / 为什么需要这个技能

Unity's `FrameDebuggerUtility` (in `UnityEditorInternal`) is an **internal API** — not documented, not guaranteed across versions, and
requiring reflection or `InternalAPIEditorBridge` assemblies to access. The reflection approach makes your code portable across Unity
versions and removes the dependency on fragile bridge assemblies.

Unity 的 `FrameDebuggerUtility` 是内部 API，需要通过反射或 InternalAPIEditorBridge 访问。本技能教您使用反射方式封装，使代码跨 Unity 版本兼容。

This skill teaches how to / 本技能涵盖以下内容：

1. **Reflect into FrameDebuggerUtility** — access internal APIs via delegate-based reflection
2. **Capture all draw-call events** for the current frame / 逐帧捕获所有 drawcall 事件
3. **Extract shader/pass/keyword data** from each event / 提取每个事件的 shader/pass/keyword 数据
4. **Parse shader pass metadata** via `ShaderUtil.GetShaderData` / 解析 shader pass 元数据
5. **Combine everything** into a pass-aware shader variant collector / 合并构建带精确 pass 索引的变体收集器

## The FrameDebugger Capture Pattern / 捕获模式

The core insight is that `FrameDebuggerUtility.limit` controls which event's data is populated for `GetFrameEventData`. Without setting
`limit` per-event, you only get data for the last event. The capture flow is:

核心原理：`limit` 属性控制哪个事件的数据会被 `GetFrameEventData` 填充。必须为每个事件单独设置 limit。

```
Enable FrameDebugger  →  Wait for events (RepaintAllViews + editor frames)
→  For each event i:  Set limit = i+1, Wait 1 frame, GetFrameEventData(i, data), Extract shader/pass/keywords
→  Disable FrameDebugger
```

### Key APIs (Internal — accessed via reflection)

| Method/Property | Purpose / 用途 |
|---|---|
| `FrameDebuggerUtility.SetEnabled(bool, int)` | Enable/disable with remote player GUID |
| `FrameDebuggerUtility.GetRemotePlayerGUID()` | Returns -1 for local player |
| `FrameDebuggerUtility.GetFrameEvents()` | Returns FrameDebuggerEvent[] for current frame |
| `FrameDebuggerUtility.count` | Total number of captured frame events |
| `FrameDebuggerUtility.limit` | **Critical / 关键**: controls which event is "selected" for data population |
| `FrameDebuggerUtility.GetFrameEventData(int, FrameDebuggerEventData)` | Populates data object for event at limit-1 |
| `FrameDebuggerUtility.GetFrameEventInfoName(int)` | Event display name (works without limit) |
| `FrameDebuggerUtility.GetFrameEventObject(int)` | UnityEngine.Object for the event (works without limit) |

### FrameDebuggerEventData Fields (Internal)

| Field | Content / 内容 |
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

## Step-by-Step: Writing the Code / 分步编写指南

### Step 1: Reflection Wrapper Class / 反射封装类

Create a static class (e.g. `FrameDebuggerReflect`) that resolves all internal types and members once in its static constructor.

创建静态类，在静态构造函数中一次性解析所有内部类型和成员。

The internal types to resolve / 需要解析的内部类型：
```
UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility (static utility class)
UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData (event data class)
```

See `references/reflection-wrapper.md` for the complete template. Key patterns / 关键模式：

- Use `typeof(Editor).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility")` to find the type
- Use `Delegate.CreateDelegate` for static methods and property getters/setters — ~10x faster than `MethodInfo.Invoke`
- Use `MethodInfo.Invoke` for `GetFrameEventData` because its parameter type (`FrameDebuggerEventData`) is only resolvable at runtime
- Use `Activator.CreateInstance(type, true)` to create instances of internal types (the `true` bypasses constructor visibility)
- **Always null-check delegates**; log once and degrade gracefully — don't crash the Editor if reflection fails

### Step 2: The Capture Loop / 捕获循环

Use `EditorApplication.update` as the tick mechanism. The loop state machine / 使用 `EditorApplication.update` 作为驱动机制：

1. **Phase 1 — Wait for events**: After enabling FrameDebugger, call `InternalEditorUtility.RepaintAllViews()` until
   `GetFrameEvents()` returns non-null or `count > 0`.

2. **Phase 2 — Iterate events**: Set `limit = targetIndex + 1`, wait one editor frame, then call
   `GetFrameEventData(targetIndex, data)` and extract fields. Repeat for all events.

3. **Phase 3 — Done**: Remove update handler, disable FrameDebugger, call completion callback.

See `references/capture-loop.md` for the complete pattern.

### Step 3: Extract Shader Pass Metadata / 提取 Pass 元数据

Use `ShaderUtil.GetShaderData(shader)` (public Editor API — no reflection needed) to get structured pass information:

使用公开的 `ShaderUtil.GetShaderData` API 提取结构化的 pass 信息：

```
ShaderData → SubshaderCount → GetSubshader(i) → PassCount → GetPass(j) → pass.Name
```

The global `passIndex` is the flattened index across all subshaders / 全局 passIndex 是所有 subshader 的扁平化索引：
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

To extract the LightMode tag / 提取 LightMode 标签：
`pass.GetType().GetMethod("FindTagValue").Invoke(pass, new[] { new ShaderTagId("LightMode") })`.

### Step 4: Combine Into a Variant Collector / 合并为变体收集器

Merge FrameDebugger capture results with ShaderUtil pass metadata / 合并 FrameDebugger 捕获结果与 pass 元数据：

1. **Match by passIndex first** / 优先按 passIndex 匹配 — use `ShaderPassExtractor.FindByIndex(shader, passIndex)`
2. **Fallback matching** / 降级匹配 — when passIndex is -1 (unknown), call `FindBestMatch(shader, passType, passName, lightMode)`:
   - Priority / 优先级: `passName` → `lightMode` → `PassType`
3. **Normalize keywords** / 关键词归一化 — sort and deduplicate for stable comparison
4. **Deduplicate** / 去重 — use composite key: `shader|passIndex|keywords|passName|lightMode`
5. **Output** / 输出 — save one JSON/ScriptableObject per shader

> **IMPORTANT / 重要**: See `references/recorder.md` for the complete record data model and merge logic. The `FindBestMatch` fallback chain is critical for production robustness — FrameDebugger's passIndex is not always available (e.g., non-rendering events like "Clear" or certain instancing paths). Always implement all 3 fallback levels.

## Important Caveats / 重要注意事项

- **`limit` IS mandatory / `limit` 是必须的**: `GetFrameEventData` only returns valid data for the event at `limit - 1`. You must set limit per-event.
- **One frame wait / 等待一帧**: After changing `limit`, you need one EditorApplication.update tick before `GetFrameEventData` returns true.
- **FrameDebugger re-enable / 重启用**: If FrameDebugger was already enabled, disable then re-enable for a clean capture state.
- **Event count 0 / 事件数为0**: If `GetFrameEvents()` returns null/empty, call `RepaintAllViews()` to trigger event population.
- **No batch mode / 不支持批处理模式**: FrameDebugger requires an active Editor GameView. It does not work in `-batchmode`.
- **Performance / 性能**: Each limit change + 1 frame wait = N frames for N events. Complex scenes with 500+ events will take 500+ frames.

## PassType Guessing / 当 passIndex 未知时的降级策略

When FrameDebugger cannot determine the passIndex (-1), you need to guess the `PassType` from available hints:

当 FrameDebugger 无法确定 passIndex 时，需要从 passName 或 lightMode 推断 PassType：

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

Without this guessing logic, variants from ShadowCaster/Meta/Deferred passes would be incorrectly assigned to `PassType.Normal`,
causing the shader variant stripper to retain the wrong variants (or strip the right ones). This is a critical gap that eval
runs showed is easy to miss — **always include it**.

> 如果没有此推断逻辑，ShadowCaster/Meta/Deferred 等 pass 的变体将被错误分配为 `PassType.Normal`，导致编译保留错误的变体。

## Composite Deduplication Key / 复合去重键

FrameDebugger can capture the same shader variant multiple times (same object rendered in multiple passes, or across frames).
Use a composite key to deduplicate:

同一 shader 变体可能被多次捕获（多 pass 渲染或多帧），必须使用复合键去重：

```csharp
string dedupKey = $"{shader.name}|{passIndex}|{keywordKey}|{passName}|{lightMode}";
```

Why all 5 fields? / 为什么需要全部 5 个字段：
- `shader` — different shaders have different pass layouts / 不同 shader 有不同的 pass 布局
- `passIndex` — same PassType can appear in multiple passes (e.g., two `PassType.Normal` passes) / 同一 PassType 可能出现在多个 pass 中
- `keywords` — same pass with different keyword combinations are different variants / 不同关键字的变体不同
- `passName` — FrameDebugger may report passIndex=-1; passName disambiguates / 当 passIndex 不可用时辅助判断
- `lightMode` — extra disambiguation when both passIndex and passName are unreliable / 额外消歧

**Common mistake / 常见错误**: Using only `(shader, passType, keywords)` like Unity's built-in SVC does.
This collapses variants from different passes with the same PassType into one, defeating the purpose of pass-aware collection.
Always include passIndex and passName in the dedup key.

> 只使用 `(shader, passType, keywords)` 去重会丢失 pass 级别的精度，这是最常见的错误。

## Reference Files / 参考文件

- `references/reflection-wrapper.md` — Complete FrameDebuggerReflect template with all field accessors and StartCapture
- `references/capture-loop.md` — Test script showing the full capture loop pattern
- `references/shader-metadata.md` — Shader pass metadata extractor with PassType guessing
- `references/recorder.md` — Complete variant recorder combining FD capture + pass metadata

---

## 中文说明 / Chinese Documentation

### 技能概述

本技能指导如何通过反射方式访问 Unity Editor 内部的 FrameDebuggerUtility API，实现逐帧渲染数据捕捉，主要包括：

1. **FrameDebuggerReflect 反射封装**：使用 `Delegate.CreateDelegate` 将内部 API 绑定为高性能委托，避免依赖 `InternalAPIEditorBridge`
2. **捕获循环**：使用 `EditorApplication.update` 驱动状态机，逐帧遍历所有渲染事件
3. **数据提取**：从每个渲染事件中提取 shader 名称、pass 索引、pass 名称、LightMode、shader 关键字等
4. **Pass 元数据提取**：使用 `ShaderUtil.GetShaderData` 获取 shader 的所有 pass 信息
5. **变体收集器**：合并 FrameDebugger 捕获数据与 pass 元数据，生成带精确 passIndex 的变体集合

### 关键原理

- `FrameDebuggerUtility.limit` 控制哪个事件的数据被填充——必须为每个事件单独设置
- 修改 limit 后需等待一帧 Editor 更新才能使 GetFrameEventData 返回有效数据
- FrameDebugger 需要一个活跃的 GameView 窗口，在 `-batchmode` 下不可用
- `GetFrameEventData` 的内部参数类型只能在运行时解析，需使用 `MethodInfo.Invoke`
- 全局 passIndex 是所有 subshader 的扁平化索引（从 0 递增）

### 工作流程

```
1. 创建 FrameDebuggerReflect 静态类，通过反射解析所有内部类型
2. 创建捕获循环（EditorApplication.update 状态机）
3. 提取 ShaderUtil.GetShaderData 的 pass 元数据
4. 合并 FD 数据 + pass 元数据 + 去重 → 输出变体收集结果
```
