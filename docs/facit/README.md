# The facit (structured)

The facit is the **single source of truth** for what we build — strict typed JSON validated
against a JSON Schema. **No markdown.** Stories, acceptance criteria, the implementation map,
the gap, and the lock are all structured data.

This repo is a single application, so the tree has exactly one scope:

```
docs/facit/
  schema/               facit / implementation / gap / lock schemas (vendored, + `app` level)
  app/facit.json        the product facit (scope: app, extends: null)
  app/implementation.json   per-AC status + verified code/test evidence
  app/gap.json          per-AC gapType + severity + action (the build backlog)
  facit.lock.json       the baseline snapshot (node id → content-hash + proven-status + tests)
```

## IDs

Unique, immutable, parent-bound: `E{n}.S{k}.A{m}` (epic → story → AC). `k` and `m` are assigned
once at creation and **never renumber**, even when reordered — so the lock and the AC→test map
key on them safely. Globally qualified as `app::E1.S1.A1`.

## The loop

`facit → implementation → gap → build`. Stories are truth; never design from current code. When
an AC is proven by a passing test (`facit prove` with TRX + coverage from `dotnet test`), its
lock entry becomes proven; thereafter `facit diff` shows only what changed, so you re-prove only
that. (How we work end-to-end: [`/CLAUDE.md`](../../CLAUDE.md).)
