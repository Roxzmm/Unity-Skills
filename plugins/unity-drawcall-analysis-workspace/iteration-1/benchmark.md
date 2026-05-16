# Benchmark: unity-drawcall-analysis (Iteration 1)

## Overall Results

| Config | Pass Rate | Avg Time (s) | Total Tokens |
|---|---|---|---|
| With Skill | 87.5% | 189.3 | 214372 |
| Without Skill (Baseline) | 83.3% | 139.7 | 194938 |

**Delta (With Skill - Baseline):** Pass rate +4.2%, Time +49.6s

## Per-Eval Breakdown

| Eval | With Skill | Baseline | Delta |
|---|---|---|---|
| reflection-wrapper | 100% (113s) | 100% (117s) | +0% |
| capture-test-script | 100% (54s) | 100% (94s) | +0% |
| variant-recorder | 62% (401s) | 50% (208s) | +12% |

## Key Observations

1. **Eval 0 (reflection-wrapper):** Both skill and baseline scored 100%. Skill was slightly faster (113s vs 117s).
2. **Eval 1 (capture-test-script):** Both scored 100%. Skill was **significantly faster** (54s vs 94s, ~1.7x).
3. **Eval 2 (variant-recorder):** Skill scored higher (62.5% vs 50%) but took longer (401s vs 208s). The with-skill agent produced more complete pass type handling. Both lacked proper PassType guessing and composite dedup keys.