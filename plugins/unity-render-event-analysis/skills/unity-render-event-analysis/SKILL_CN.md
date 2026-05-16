---
name: unity-render-event-analysis
description_zh: >
  分析 Unity Editor 逐帧渲染指令数据。通过反射封装 FrameDebuggerUtility 捕获每帧所有 drawcall 的 shader/pass/keyword 信息，
  使用 ShaderUtil.GetShaderData 提取 pass 元数据，构建自定义 shader variant 收集器。不依赖 InternalAPIEditorBridge。
---

# Unity Render Event Analysis — 逐帧渲染数据捕获与分析

## 为什么需要这个技能

Unity 的 `FrameDebuggerUtility`（位于 `UnityEditorInternal`）是**内部 API**——未文档化、不保证跨版本兼容，需要反射或 `InternalAPIEditorBridge` 程序集来访问。使用反射方式使你的代码跨 Unity 版本兼容，并消除对脆弱桥接程序集的依赖。

本技能涵盖以下内容：

1. **FrameDebuggerReflect 反射封装** — 使用基于委托的反射访问内部 API
2. **逐帧捕获所有 drawcall 事件**
3. **从每个事件提取 shader/pass/keyword 数据**
4. **通过 `ShaderUtil.GetShaderData` 解析 shader pass 元数据**
5. **合并构建带精确 pass 索引的变体收集器**

## FrameDebugger 捕获模式

核心原理：`FrameDebuggerUtility.limit` 控制哪个事件的数据会被 `GetFrameEventData` 填充。如果不为每个事件单独设置 limit，你只能获取最后一个事件的数据。捕获流程如下：

```
启用 FrameDebugger → 等待事件产生 (RepaintAllViews + editor frames)
→ 对每个事件 i: 设置 limit = i+1, 等待 1 帧, GetFrameEventData(i, data), 提取 shader/pass/keywords
→ 禁用 FrameDebugger
```

### 关键 API（内部 API，通过反射访问）

| 方法/属性 | 用途 |
|---|---|
| `FrameDebuggerUtility.SetEnabled(bool, int)` | 启用/禁用，附带远程 player GUID |
| `FrameDebuggerUtility.GetRemotePlayerGUID()` | 本地 player 返回 -1 |
| `FrameDebuggerUtility.GetFrameEvents()` | 返回当前帧的 FrameDebuggerEvent[] |
| `FrameDebuggerUtility.count` | 捕获的帧事件总数 |
| `FrameDebuggerUtility.limit` | **关键**：控制哪个事件被"选中"用于数据填充 |
| `FrameDebuggerUtility.GetFrameEventData(int, FrameDebuggerEventData)` | 填充 limit-1 位置的事件数据 |
| `FrameDebuggerUtility.GetFrameEventInfoName(int)` | 事件显示名称（无需 limit） |
| `FrameDebuggerUtility.GetFrameEventObject(int)` | 事件的 UnityEngine.Object（无需 limit） |

### FrameDebuggerEventData 字段（内部）

| 字段 | 内容 |
|---|---|
| `m_OriginalShaderName` | 材质上指定的原始 shader 名称 |
| `m_RealShaderName` | 实际使用的 shader（可能因渲染 pass 而异） |
| `m_PassName` | Shader 中的 pass 名称 |
| `m_PassLightMode` | LightMode 标签值 |
| `m_ShaderInstanceID` | Shader 对象的实例 ID（使用 `EditorUtility.InstanceIDToObject`） |
| `m_SubShaderIndex` | 当前活跃的 subshader |
| `m_ShaderPassIndex` | 所有 subshader 的全局 pass 索引 |
| `shaderKeywords` | 以空格分隔的材质关键字字符串 |
| `m_Mesh` | 正在渲染的网格 |
| `m_VertexCount` / `m_IndexCount` / `m_InstanceCount` / `m_DrawCallCount` | 渲染统计 |
| `m_ShaderInfo` | 嵌套对象，包含详细 shader 信息（关键词、浮点数、纹理等） |

### 嵌套 ShaderInfo 关键字

`m_ShaderInfo.m_Keywords[]` 是关键字结构体数组：
- `m_Name` — 关键字字符串
- `m_Flags` — 关键字标志
- `m_IsGlobal` — 是否为全局关键字
- `m_IsDynamic` — 是否为动态关键字

## 分步编写指南

### 第 1 步：反射封装类

创建静态类（如 `FrameDebuggerReflect`），在静态构造函数中一次性解析所有内部类型和成员。

需要解析的内部类型：
```
UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility（静态工具类）
UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData（事件数据类）
```

完整模板见 `references/reflection-wrapper.md`。关键模式：

- 使用 `typeof(Editor).Assembly.GetType("...")` 查找类型
- 对静态方法和属性使用 `Delegate.CreateDelegate` — 比 `MethodInfo.Invoke` 快约 10 倍
- `GetFrameEventData` 使用 `MethodInfo.Invoke` — 其参数类型 `FrameDebuggerEventData` 只能在运行时解析
- 使用 `Activator.CreateInstance(type, true)` 创建内部类型实例（`true` 绕过构造函数可见性）
- **始终对委托进行空检查**；记录一次日志并优雅降级——不要因反射失败导致编辑器崩溃

### 第 2 步：捕获循环

使用 `EditorApplication.update` 作为驱动机制。循环状态机：

1. **阶段 1 — 等待事件**：启用 FrameDebugger 后，在 update 循环中调用 `InternalEditorUtility.RepaintAllViews()`
   直到 `GetFrameEvents()` 返回非空或 `count > 0`。
2. **阶段 2 — 遍历事件**：设置 `limit = targetIndex + 1`，等待一帧编辑器更新，然后调用
   `GetFrameEventData(targetIndex, data)` 提取字段。对所有事件重复此操作。
3. **阶段 3 — 完成**：移除 update 处理程序，禁用 FrameDebugger，调用完成回调。

完整模式见 `references/capture-loop.md`。

### 第 3 步：提取 Shader Pass 元数据

使用 `ShaderUtil.GetShaderData(shader)`（公共 Editor API，无需反射）获取结构化的 pass 信息：

```
ShaderData → SubshaderCount → GetSubshader(i) → PassCount → GetPass(j) → pass.Name
```

全局 `passIndex` 是所有 subshader 的扁平化索引：
```csharp
int passIndex = 0;
for (int sub = 0; sub < shaderData.SubshaderCount; sub++)
{
    var subShader = shaderData.GetSubshader(sub);
    for (int local = 0; local < subShader.PassCount; local++)
    {
        var pass = subShader.GetPass(local);
        // passIndex = 全局索引; local = 该 subshader 内的索引
        passIndex++;
    }
}
```

提取 LightMode 标签：
`pass.GetType().GetMethod("FindTagValue").Invoke(pass, new[] { new ShaderTagId("LightMode") })`

完整实现（含 `PassType` 推断和 `FindBestMatch` 降级）见 `references/shader-metadata.md`。

### 第 4 步：合并为变体收集器

合并 FrameDebugger 捕获结果与 ShaderUtil pass 元数据。关键决策：pass 身份解析。

**优先级链**（将捕获的 drawcall 匹配到 shader pass 时）：
1. **按 passIndex**（如果 `>= 0`）— 使用 `ShaderPassExtractor.FindByIndex(shader, passIndex)`，精确匹配
2. **按 passName** — 当 passIndex 为 -1 时的降级方案
3. **按 lightMode** — 当 passName 也缺失时的降级方案
4. **按 PassType** — 最后的兜底方案

然后对关键词进行归一化（排序、去重），并使用复合键去重：
```
shader|passIndex|keywords|passName|lightMode
```

> **常见错误**：只使用 `(shader, passType, keywords)` 像 Unity 内置 SVC 那样。这会将同一 PassType 下不同 pass 的变体合并，失去 pass 级别精度的意义。始终包含 passIndex 和 passName。

完整的数据模型、合并逻辑和关键词归一化工具见 `references/recorder.md`。

## 重要注意事项

- **`limit` 是必须的**：`GetFrameEventData` 只为 `limit - 1` 处的事件返回有效数据。必须为每个事件单独设置 limit。
- **等待一帧**：修改 limit 后，需要一次 EditorApplication.update 才能让 GetFrameEventData 返回 true。
- **重启用 FrameDebugger**：如果 FrameDebugger 已经启用，先禁用再重新启用以获得干净的捕获状态。
- **事件数为 0**：如果 `GetFrameEvents()` 返回 null/空，调用 `RepaintAllViews()` 触发事件填充。
- **不支持批处理模式**：FrameDebugger 需要活跃的 Editor GameView，在 `-batchmode` 下不可用。
- **性能**：每次更改 limit + 等待 1 帧 = N 帧处理 N 个事件。复杂场景 500+ 事件需要 500+ 帧。

## 当 passIndex 未知时的 PassType 推断

当 FrameDebugger 无法确定 passIndex 时，需要从 passName 或 lightMode 推断 PassType：

```csharp
static PassType GuessPassType(string passName, string lightMode)
{
    var value = !string.IsNullOrEmpty(lightMode) ? lightMode : passName;
    if (string.IsNullOrEmpty(value)) return PassType.Normal;

    if (value.IndexOf("ShadowCaster", OrdinalIgnoreCase) >= 0)
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

没有此逻辑时，ShadowCaster/Meta/Deferred 等 pass 的变体将被错误分配为 `PassType.Normal`，导致编译时保留错误变体或剔除正确变体。评估运行显示这是最容易被遗漏的关键环节——**务必包含此项**。

## 复合去重键

FrameDebugger 可能多次捕获同一 shader 变体（同一对象被多次渲染，或跨多帧）。使用复合键去重：

```csharp
string dedupKey = $"{shader.name}|{passIndex}|{keywordKey}|{passName}|{lightMode}";
```

为什么需要全部 5 个字段：
- `shader` — 不同 shader 有不同的 pass 布局
- `passIndex` — 同一 PassType 可能出现在多个 pass 中（如两个 `PassType.Normal`）
- `keywords` — 同一 pass 的不同关键字组合是不同的变体
- `passName` — FrameDebugger 可能报告 passIndex=-1，passName 辅助判断
- `lightMode` — 当 passIndex 和 passName 均不可靠时的额外消歧

## 参考文件

- `references/reflection-wrapper.md` — 完整的 FrameDebuggerReflect 模板，包含所有字段访问器和 StartCapture
- `references/capture-loop.md` — 展示完整捕获循环模式的测试脚本
- `references/shader-metadata.md` — Shader pass 元数据提取器，包含 PassType 推断和 FindBestMatch 降级
- `references/recorder.md` — 完整变体记录器，结合 FD 捕获 + pass 元数据，含去重和关键词归一化
