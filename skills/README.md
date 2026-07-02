# skills

Skills auto-loaded by Claude Code when you open this repo. They are about **how we work**, not
what we build — the product lives in the facit ([`docs/facit/app/facit.json`](../docs/facit/app/facit.json))
and in how it's implemented ([`docs/facit/app/implementation.json`](../docs/facit/app/implementation.json)).
See [`CLAUDE.md`](../CLAUDE.md) for the full operating manual.

| skill | when it fires |
|---|---|
| [user-story-driven-development](user-story-driven-development/SKILL.md) | Starting any substantive change — feature, fix, refactor, audit. The stories → gap → build loop and how it composes with the other skills. |
| [keeping-the-facit-and-gap-honest](keeping-the-facit-and-gap-honest/SKILL.md) | Writing/refreshing stories in the facit, updating the implementation map, or running a gap analysis. The mechanics of keeping the facit true and the gap real. |
| [scaling-with-agents](scaling-with-agents/SKILL.md) | A task is too large for one context, has parallelisable parts, or needs findings verified before they're trusted. Decompose, fan out, adversarially verify. |

These wrap and point to the installed general skills (`brainstorming`, `writing-plans`,
`subagent-driven-development`, `test-driven-development`) rather than duplicating them.

## Adding a new skill

1. Create `skills/<name>/SKILL.md` with frontmatter:
   ```yaml
   ---
   name: <kebab-case>
   description: One-line trigger condition. Specific. Says when to fire.
   ---
   ```
2. Keep the body about **how we work** — the situational decision or method the skill captures.
   Never describe the product; that belongs in the stories.
3. Add a row to the table above.
