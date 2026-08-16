---
name: keeping-the-facit-and-gap-honest
description: Use when writing or refreshing stories in the JSON facit (docs/facit/app/), updating implementation.json, or running a gap analysis. The mechanics of keeping the facit true and the gap real.
---

# Keeping the facit and the gap honest

The facit ([`docs/facit/app/facit.json`](../../docs/facit/app/facit.json)) is the truth of what
we build — strict typed JSON validated against [`docs/facit/schema/`](../../docs/facit/schema/).
The [`implementation.json`](../../docs/facit/app/implementation.json) is how each acceptance
criterion is realised today; the [`gap.json`](../../docs/facit/app/gap.json) is the difference.
They are only useful if they stay honest. This skill is the mechanics for that.

## Writing a story

- One story = `{ role, want, soThat, acceptanceCriteria[] }` — *As a [role], I want [capability],
  so that [benefit]* with numbered ACs in Given / When / Then form. Every AC is concrete and
  falsifiable — it must map directly to a test.
- **Conceptual vocabulary only.** Name the domain ideas (link, token, trigger URL, allowance,
  audit trail), not type/file/method names. A reader should be able to regenerate the system
  from the facit without seeing the code.
- Stories describe the *desired* truth, written with the product in view. If today's code
  differs, that is a gap to record — not a reason to soften the story.
- IDs are unique, immutable, parent-bound (`E{n}.S{k}.A{m}`) — assigned once, never renumbered.
  Run `python3 tools/facit/facit.py validate` (must stay green) and commit; never leave a
  refinement only in chat or memory.

## Keeping `implementation.json` honest

- For each AC, record **how it is implemented today** — grounded in real code/test reads
  (`file:line`, test id), not paraphrase and not memory. Status is `implemented | partial |
  missing | obsolete`; evidence is verified references.
- "It builds" or "a report said done" is **not** evidence an AC holds. Re-check against the AC,
  against the running behaviour where you can.
- Record honestly what is partial, stubbed, or missing. An overstated "done" hides work.

## Running a gap analysis

- The gap (`gap.json`) is **facit vs. implementation**, re-derived from the *current code* every
  time — never trusted from a previous snapshot.
- Don't let it become "what's the smallest change to the current code." The gap is measured
  against the facit; the fix is whatever the AC requires, including rewrite or deletion.
- Each gap item names the AC id it serves and what's missing — that is the input to
  `writing-plans`.
- When the gap reveals the facit itself is wrong or incomplete, fix the facit first, then
  re-derive. The facit always wins a conflict.

## Cadence + the lock + proving

- `python3 tools/facit/facit.py validate` is the gate; `… lock` snapshots the state; `… diff`
  shows only what changed since the lock; `… status` summarises coverage.
- An AC becomes **proven** via `… prove --impl docs/facit/app/implementation.json --results
  <trx> --coverage <cobertura.xml>` after `dotnet test --logger trx --collect:"XPlat Code
  Coverage"` — the proving test is the verdict, the recorded covered-file hashes only the
  re-run trigger (`… conform` checks them; `… prove --verify` re-checks the bindings).
- After each meaningful step: update the facit if the vision moved (commit), re-derive the gap
  from the code, and challenge whether prior "proven" claims still hold.
