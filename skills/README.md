# Claude Code Skills for PurrDiction

This folder contains [Claude Code](https://docs.anthropic.com/en/docs/claude-code) skills — specialized AI development guidance for writing correct PurrDiction code.

## Usage

Copy the skills into your project's `.claude/skills/` directory:

```bash
cp -r skills/purrdiction-csp /path/to/your-project/.claude/skills/
```

Once in place, the skill auto-activates when Claude Code detects relevant keywords (e.g., `Simulate`, `PredictedRandom`, `IPredictedData`, `isReplaying`, `rollback`).

## Available Skills

| Skill | Covers |
|---|---|
| `purrdiction-csp` | Determinism rules, state design, side-effect dispatch, reconciliation gotchas, tick execution model |
