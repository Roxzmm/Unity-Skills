# unity-render-event-analysis

Analyze per-frame Unity Editor draw-call data via FrameDebugger reflection.

通过反射封装 FrameDebugger，分析 Unity Editor 逐帧渲染指令数据的 skill。

## Skills

- **unity-render-event-analysis** — Capture shader/pass/keyword info from rendered objects, extract pass metadata, build custom variant collectors
- **unity-render-event-analysis**（中文）— 捕获渲染对象的 shader/pass/keyword 信息，提取 pass 元数据，构建自定义变体收集器

## Requirements

- Unity 2022.3+ (any project with Editor scripts)
- No external dependencies — all internal APIs accessed via reflection
- 无需外部依赖 — 所有内部 API 均通过反射访问

## Files

```
skills/unity-render-event-analysis/
├── SKILL.md              # Skill instructions (技能说明)
├── references/
│   ├── reflection-wrapper.md   # FrameDebuggerReflect template (反射封装模板)
│   ├── capture-loop.md         # Capture loop test script (捕获循环测试脚本)
│   ├── shader-metadata.md      # ShaderUtil.GetShaderData pass extraction (Pass 元数据提取)
│   └── recorder.md             # Combined variant recorder pattern (变体记录器)
└── evals/
    └── evals.json              # Test cases (测试用例)
```
