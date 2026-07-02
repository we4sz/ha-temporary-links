---
name: scaling-with-agents
description: Use when a task is too large for one context, has independent parts that can run in parallel, or needs findings verified before they're trusted. How to decompose, fan out, and adversarially verify with agents and workflows.
---

# Scaling with agents

When work outgrows a single context — a broad audit, a migration across many sites, a design that
needs independent perspectives, a research question with many angles — don't grind it serially.
Decompose it, fan it out across agents, and verify before you trust. This skill is how.

## When to reach for it

- **Breadth:** the answer means sweeping many files / specs / subsystems. One reader per slice,
  in parallel, returns conclusions — not file dumps — to the coordinator.
- **Confidence:** a finding would be expensive to get wrong. Generate it, then have independent
  agents try to *refute* it before acting.
- **Scale:** the work doesn't fit one context (a migration, an audit, a corpus pass). Pipeline
  the work-list across agents.

## How to do it

- **Scout, then fan out.** Discover the work-list inline first (list the files, scope the gap),
  *then* fan out over it — you don't need to know the shape before the task, only before the
  orchestration step.
- **Coordinator keeps the conclusion, not the dumps.** A subagent's final message comes back to
  you; relay what matters, don't paste raw output forward.
- **Adversarially verify.** For any finding that drives a decision, spawn independent verifiers
  prompted to refute it (give each a distinct lens — correctness, security, does-it-reproduce —
  rather than N identical checks). Kill findings that don't survive.
- **Loop until dry / until target.** For unknown-size discovery, keep going until consecutive
  rounds find nothing new; for sized work, accumulate to the target. Don't silently cap coverage —
  if you bound it, say what was dropped.

## Model tiering still applies

- **The strongest available model** does the decomposition, the design, and the review.
- **Cheaper models** do the mechanical work — each given a task clear enough that any model can
  run it (exact inputs, exact expected output). If a subagent needs the top model to interpret
  its task, the task is under-specified; fix the spec, don't upgrade the model.

## Tools

- The **Agent** tool for individual subagents; send independent ones in a single message so they
  run in parallel.
- The **Workflow** tool for deterministic multi-agent orchestration (fan-out, pipelines,
  loop-until-dry, judge panels). Author the structure; let cheaper models fill the stages.
- Compose with **`subagent-driven-development`** to execute a plan task-by-task with review gates.

This skill is for *how to run the agents*. What they build still comes from the stories and the
gap (see `user-story-driven-development`).
