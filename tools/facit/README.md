# `facit` CLI

Manages a structured facit tree — compiles every `facit.json` under a root directory into
**one** view keyed by globally-qualified node ids, and locks/diffs the whole tree so you
re-prove only what changed.

The default root is `docs/facit/` (the platform facit). Use `--root` to point the CLI at
any other facit tree, including its own spec (`tools/facit/spec/`).

```
python3 tools/facit/facit.py [--root <dir>] <command>
```

## Global option

| option | description |
|---|---|
| `--root <dir>` | Facit root directory. Default: `docs/facit` (resolved from the repo root). Relative paths are resolved from the repo root. The lock file is `<root>/facit.lock.json`. The JSON Schema is always loaded from `docs/facit/schema/` regardless of `--root`. |

## Commands

| command | what it does |
|---|---|
| `compile [--out F]` | discover + schema-validate every facit, check integrity, report counts + `facitHash`. `--out` writes the compiled view. |
| `validate` | compile as a pass/fail gate (exit 1 on any error). Use in CI. |
| `lock` | snapshot the compiled whole → `<root>/facit.lock.json` (per-node content-hash + proven status + tests). One lock over all facits. Proven status carries forward only for unchanged nodes. |
| `diff` | compare the live compiled facit to the lock → added / changed / removed nodes. |
| `status` | coverage summary (nodes, locked, proven, changed-since-lock). |
| `gap [F]` | with no arg: list every acceptance-criterion id (so a gap can be built on the compiled whole). With a `gap.json`/`implementation.json`: validate its `acId`s resolve + report uncovered ACs. |

## Examples

```sh
# Platform facit (default)
python3 tools/facit/facit.py compile
python3 tools/facit/facit.py validate

# CLI's own facit (dogfood — E1.S4.A3)
python3 tools/facit/facit.py --root tools/facit/spec compile
python3 tools/facit/facit.py --root tools/facit/spec validate
python3 tools/facit/facit.py --root tools/facit/spec lock
python3 tools/facit/facit.py --root tools/facit/spec diff

# Absolute path
python3 tools/facit/facit.py --root /path/to/my-project/facit compile
```

## Node ids

Local to each facit: `E{n}.S{k}.A{m}` (immutable, parent-bound). Globally qualified as
`<scopeRef>::<id>`:

- `engine::E1.S1.A1`
- `utility:crawl::E1.S2.A3`
- `domain:apimapping::E1.S1`
- `domain:apimapping/target:openapi-spec::E1.S1.A1`

One lock file covers every node, but a change to one component only changes that component's
node hashes — so improving `crawl` re-opens only `crawl`'s nodes in the diff.

## The loop

Build the implementation up to the facit → when a node is proven (its AC has a passing test),
its lock entry becomes `proven` → `diff` thereafter shows only what changed since the lock, so
re-verification is incremental, not whole-tree.

## Provenance

Vendored from [openapimapper](../../../openapimapper/tools/facit/) (2026-07-02) with these
local divergences — carry them forward on the next re-sync, they are not upstream yet:

- The `app` scope level (schema enum + `scope_ref`) for single-scope facit trees like this
  repo's — one application, one `facit.json`, `extends: null`.
- **`prove` refuses a mass demotion.** If a run's coverage filtering would demote EVERY
  currently-proven lock node at once, `prove` now fails loudly (non-zero exit, no lock write)
  instead of silently wiping the whole proven set — that pattern is almost always a
  configuration error (e.g. `--src-root` / `facit.config.json`'s `srcRoot` resolving to the
  wrong directory and filtering all coverage out), not a genuine regression across every AC
  simultaneously. Override with `--allow-mass-demote` for the rare case a whole-set demotion
  really is intended. (Root cause of the original bug: with no `facit.config.json` present,
  `_resolve_src_root`'s default — the facit root's parent — silently filtered out all `src/**`
  coverage for a facit nested below the repo root like `docs/facit`; the fix is two-sided —
  this refusal, plus always shipping a `facit.config.json` alongside a nested facit root that
  pins the real `srcRoot`.)
- **`conform` warns when a proven node has no `testSources`.** The proving-test-source
  drift-check (E1.S12.A5/A6) was silently a no-op for any proven node recorded without
  `testSources` (e.g. proven before `testRoots` was configured, or when the proving test
  couldn't be located under `testRoots`). `conform` now prints a distinct
  `WARN-NO-TEST-SOURCE` line per such node so the blind spot is visible — it does not affect
  the conformant/drifted/unverifiable counts or exit code.
- **`_parse_trx` aggregates by worst outcome per fully-qualified test name.** Previously, when
  several `<UnitTestResult>` entries shared one FQN — as every parameterized case of an xUnit
  `[Theory]` does, since `TestMethod`'s `className`+`name` carry no parameter info — the FIRST
  recorded outcome won and later results for that FQN were dropped. A `[Theory]` whose first
  case passed and a later case failed would read as `Passed` (fail-open). Now every outcome
  seen for a given FQN is collected and the FQN resolves to a non-`Passed` outcome if ANY case
  was non-`Passed`, `Passed` only if all cases were.
