# unity-render-event-analysis

通过反射封装 FrameDebugger，分析 Unity Editor 逐帧渲染指令数据的插件。

## 技能

- **unity-render-event-analysis** — 捕获渲染对象的 shader/pass/keyword 信息，提取 pass 元数据，构建自定义变体收集器

## 环境要求

- Unity 2022.3+（任意包含 Editor 脚本的项目）
- 无需外部依赖 — 所有内部 API 均通过反射访问

## 文件结构

```
skills/unity-render-event-analysis/
├── SKILL.md              # 技能说明（英文）
├── SKILL_CN.md           # 技能说明（中文）
├── references/
│   ├── reflection-wrapper.md   # FrameDebuggerReflect 反射封装模板
│   ├── capture-loop.md         # 捕获循环测试脚本
│   ├── shader-metadata.md      # ShaderUtil.GetShaderData pass 元数据提取
│   └── recorder.md             # 变体记录器完整模式
└── evals/
    └── evals.json              # 测试用例
```

## English Version

See [README.md](README.md) for the English documentation.
