---
name: user-story-driven-development
description: Use when starting any substantive change in this repo — a feature, a fix, a refactor, an audit. Establishes the stories → gap → build loop and how it composes with the other skills.
---

# User-story-driven development

This repo has exactly one development loop. Everything substantive goes through it. The product
lives in the **facit** ([`docs/facit/app/facit.json`](../../docs/facit/app/facit.json) — strict
typed JSON, single `app` scope) and in **how each acceptance criterion is implemented**
([`implementation.json`](../../docs/facit/app/implementation.json)). Code is downstream of both.
The [`facit` CLI](../../tools/facit/) validates, locks, and diffs the facit so re-proving is
incremental.

## The loop

```
1. Stories are truth      → write/refresh the relevant stories FIRST, in conceptual vocabulary,
                            with the product in view (never the code).
2. How it's implemented   → the implementation map says how each story is realised today,
                            honestly, grounded in real code reads.
3. The gap                → stories vs. implementation. THIS decides what to build AND how.
4. Build from the gap     → willing to rewrite, willing to throw code away.
```

**The one rule that makes this work:** never design from how the code looks today. "Smallest
diff", "extension over rewrite", "it rides the existing X" are not design inputs — the code does
not get a vote on the architecture. The correctness of a design comes from the stories.

## How it composes with the other skills

The loop is the spine; the general skills are the muscles. Use them in this order:

1. **`brainstorming`** — turn the idea into agreed stories + acceptance criteria. Validate by
   questions (see CLAUDE.md). The output is stories written into the facit, not a vague design
   in chat.
2. **`keeping-the-facit-and-gap-honest`** (this repo) — write the stories well, refresh
   `implementation.json`, and derive the gap from the actual code.
3. **`writing-plans`** — turn the gap into a bite-sized, model-agnostic plan.
4. **`subagent-driven-development`** — execute the plan task-by-task with fresh implementer
   subagents and review; cheaper models implement.
5. **`test-driven-development`** — every task inside the plan is TDD: failing test first. A
   passing test is what *proves* an AC (`facit prove`).
6. **`scaling-with-agents`** (this repo) — when the work is large, fan out and adversarially
   verify instead of doing it all in one context.

## Checkpoints (don't barrel ahead)

Validate with the developer at decision boundaries — before a new phase, before deleting code
that's in use, before changing a public contract (the facit, the add-on options schema, the
trigger behaviour). Use `AskUserQuestion`, one decision per question. (Full rules: CLAUDE.md.)

## The anti-patterns this skill exists to stop

- Jumping into code from a vague request without agreed stories.
- "Designing" by describing the smallest change to the current code.
- Letting the facit and `implementation.json` drift out of date while the code moves.
- Treating "it builds" as "it's done" — done means built, gated, and verified against the
  story's acceptance criteria.
