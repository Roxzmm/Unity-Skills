# unity-drawcall-analysis

Analyze per-frame Unity Editor draw-call data via FrameDebugger reflection.

## Skills

- **unity-drawcall-analysis** — Capture shader/pass/keyword info from rendered objects, extract pass metadata, build custom variant collectors

## Requirements

- Unity 2022.3+ (any project with Editor scripts)
- No external dependencies — all internal APIs accessed via reflection

## Files

```
skills/unity-drawcall-analysis/
├── SKILL.md              # Skill instructions
├── references/
│   ├── reflection-wrapper.md   # FrameDebuggerReflect template
│   ├── capture-loop.md         # Capture loop test script template
│   ├── shader-metadata.md      # ShaderUtil.GetShaderData pass extraction
│   └── recorder.md             # Combined variant recorder pattern
└── evals/
    └── evals.json              # Test cases
```
