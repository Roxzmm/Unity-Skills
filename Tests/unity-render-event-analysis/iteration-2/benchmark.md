# Benchmark: unity-render-event-analysis

## Iteration 2 vs Iteration 1

### Overall Results

| Config | Pass Rate | Avg Time (s) | Avg Tokens |
|---|---|---|---|
| **With Skill** | **100.0%** (17/17) | 88.8 ± 14.6 | 67,600 ± 4,747 |
| Without Skill | 88.2% (15/17) | 158.9 ± 16.9 | 66,652 ± 9,136 |
| **Delta** | **+11.8%** | **-70.1s (44%)** | +948 |

### Per-Eval Breakdown

| Eval | With Skill | Baseline |
|---|---|---|
| 0 - reflection-wrapper | 4/4 (100%) | 4/4 (100%) |
| 1 - capture-test-script | **5/5 (100%)** | 3/5 (60%) |
| 2 - variant-recorder | **8/8 (100%)** | 8/8 (100%) |

### Iteration 1 → 2 Improvement

| Metric | Iter 1 With Skill | Iter 2 With Skill | Δ |
|---|---|---|---|
| Pass Rate | 87.5% (21/24) | 100% (17/17) | +12.5% |
| Avg Duration | ~134.7s | 88.8s | -45.9s (34%) |

### Observations

1. The improved SKILL.md (with dedicated PassType guessing and composite dedup key sections) closed all gaps from iteration 1.
2. With-skill runs are now 100% correct across all 3 evals (17/17 assertions).
3. The skill significantly reduces generation time: 88.8s vs 158.9s baseline (-44%).
4. The baseline still struggles with eval 1 (capture loop) — it uses a fixed frame limit instead of per-event limit, and lacks the 3-phase state machine structure.
5. Token usage is similar between with-skill (67.6K) and baseline (66.7K), but the skill produces more consistently structured code.
