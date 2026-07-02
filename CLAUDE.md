# How we work in this repo

This file is about **how we work**, not **what we build**. What we build lives in the
facit — not here. Keep this file generic: a new contributor (human or agent) should be
able to work correctly from it without it ever describing the product.

If you find yourself wanting to add product detail here ("links do X", "the SMS flow is Y"),
stop — that belongs in the stories. Add it there.

---

## Where the truth lives

- **[`docs/facit/app/facit.json`](docs/facit/app/facit.json) — the facit (structured JSON).**
  The user stories + acceptance criteria as strict typed JSON validated against
  [`docs/facit/schema/`](docs/facit/schema/) — **this is the product.** This repo is a single
  application, so the tree has exactly one scope: `app` (`extends: null`). Conceptual
  vocabulary; complete enough to regenerate the system from.
- **[`implementation.json`](docs/facit/app/implementation.json) + [`gap.json`](docs/facit/app/gap.json)**
  — how each acceptance criterion is implemented today (verified code/test refs) and the gap
  (facit vs code), keyed by AC id.
- **The [`facit` CLI](tools/facit/) + `docs/facit/facit.lock.json`.**
  `python3 tools/facit/facit.py validate` is the gate; `… lock` snapshots the proven state;
  `… diff` shows only what changed since the lock, so re-proving is incremental, not
  whole-tree. `… prove` binds ACs to green tests (TRX + coverage from `dotnet test`);
  `… conform` catches code drift on proven ACs.
- **`dotnet build` + `dotnet test`** on `ha-temporary-links.sln` are the code gates.
- The facit wins. When any code/doc/comment conflicts with the facit, the facit is the truth —
  fix the code (or fix the facit first, then propagate). Never paper over a conflict.

---

## The loop: stories → gap → build

This is the only development loop. Follow it for every substantive change.

1. **Stories are truth.** Write or refresh the relevant stories in the facit *first*, in
   conceptual vocabulary. Do this with the product in view, never the code. `facit validate`
   must stay green.
2. **The implementation map shows how each story is implemented** — keep `implementation.json`
   honest, grounded in real code reads (not paraphrase, not memory).
3. **The gap = facit vs. implementation.** That comparison (`gap.json`) is what decides *what*
   to build **and how**.
4. **Build from the gap** — willing to rewrite, willing to throw code away when the gap calls
   for it. When an AC is proven (passing test), it locks; `facit diff` then re-opens only what
   changed.

**Spec-first — and what makes "proven" trustworthy.** Always change the *facit* before the
code; the facit leads, never the code. `facit diff` tracks **spec** drift — a changed AC
re-opens that node. It does **not** track **code** drift, and that is deliberate (a refactor
that preserves behaviour must not false-alarm). Code conformance is caught by the **proving
test**: an AC is *proven-and-current* only when **(a)** its facit node is unchanged since the
lock and **(b)** its proving test is green now. A red test un-proves its AC. So
**trust = facit-diff-clean (spec) + tests-green (code)**. Each proven AC records **where** it
is proven — its proving test(s) and the files those tests cover (auto-derived from coverage) —
and the gate rejects a proven AC with no test binding.

**Never design from how the code looks today.** "Smallest diff", "extension over rewrite",
"it rides the existing X" are *not* design inputs — they are how the code happens to look now,
and the code does not get a vote on the architecture. Grounding a design in current code is
legitimate only as the *"how it's implemented today"* half of the gap, never as the design
itself. The correctness of a design comes from the stories.

---

## Validate by questions — always ask, always align

**Always use the `AskUserQuestion` tool when asking the developer a question.** Never ask in
plain prose. Phrase the options so `Other` is a valid escape hatch for cases you didn't think of.

**When validating plans, brainstorming, or proposing an architecture, do it by asking
questions — not by stating conclusions:**

- Before writing a multi-step plan, ask the questions that would change the plan's *shape*.
- Before invoking an implementation skill, present the design and get an explicit approval gate.
- When you have 2–3 approaches, surface the trade-offs and ask the developer to pick.
- When intent is ambiguous, surface the ambiguity as a question — don't pick silently.

**Checkpoint at decision boundaries — never barrel ahead:** before a new phase/refactor,
before deleting code that's in use, before changing a public contract (the facit, the add-on
options schema, the trigger behaviour), before merging an architectural reframe into the plan.

If a question is genuinely tactical and answerable from the stories / the code / the
conversation, **answer it yourself.** The rule above is for the decision-points where
alignment matters more than speed.

---

## Model tiering

- **The strongest available model** — planning and review only: decompose, design, write the
  plan, review output.
- **Cheaper models** — all implementation.
- Write every implementation task **clear enough that any model can execute it**: exact paths,
  exact changes, exact gate commands, expected output. If a task needs the top model to
  *interpret* it, that's a planning gap to close — not a reason to upgrade the implementer.

---

## How we scale

- Decompose work across **agents and workflows**: fan out independent work in parallel, and
  **adversarially verify** findings before trusting them.
- Compose with the installed skills rather than reinventing them. The repo's own how-we-work
  skills live in [`skills/`](skills/) and wrap the general method:
  **brainstorming → writing-plans → subagent-driven-development → test-driven-development.**

---

## Guard-rails (durable, product-agnostic)

- **Don't bypass the gate.** No skipping tests, hooks, or CI to make something pass. A fix that
  requires disabling a test is in the wrong place.
- **Snapshot/behaviour drift on things you didn't touch** → investigate before accepting. Drift
  on something you *did* change is fine to accept with a one-line rationale.
- **Escalate after repeated failure.** ~3 failed attempts on the same step → stop and surface it
  to the developer with the evidence; the problem needs more context than you have.
- **Don't add what wasn't asked for.** A bug fix doesn't need surrounding cleanup; new ideas go
  into the facit (as stories) first, not into the current change.
- **Never commit secrets.** Tokens/credentials (HA tokens, Twilio credentials, phone numbers in
  fixtures) live in env or per-user config, never in the repo, a test, or a commit message.
- **Git:** pushing to `origin` is fine after a logical unit of work; new remotes, URL changes,
  and force-pushes need explicit approval.
- **Report outcomes faithfully.** If tests fail, say so with the output; if a step was skipped,
  say that. "Done" means built, gated, and verified — not "should work."

---

## For other agents

This file is the operating manual for any agent working in this repo.
