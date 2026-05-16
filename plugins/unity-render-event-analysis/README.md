# unity-render-event-analysis

Analyze per-frame Unity Editor draw-call data via FrameDebugger reflection.

## Skills

- **unity-render-event-analysis** — Capture shader/pass/keyword info from rendered objects, extract pass metadata, build custom variant collectors

## Requirements

- Unity 2022.3+ (any project with Editor scripts)
- No external dependencies — all internal APIs accessed via reflection

## Files

```
skills/unity-render-event-analysis/
├── SKILL.md              # Skill instructions (English)
├── SKILL_CN.md           # Skill instructions (Chinese)
├── references/
│   ├── reflection-wrapper.md   # FrameDebuggerReflect template
│   ├── capture-loop.md         # Capture loop test script
│   ├── shader-metadata.md      # ShaderUtil.GetShaderData pass extraction
│   └── recorder.md             # Combined variant recorder pattern
└── evals/
    └── evals.json              # Test cases
```

## Chinese Version / 中文版本

See [README_CN.md](README_CN.md) for the Chinese documentation.
