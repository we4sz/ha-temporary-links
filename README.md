# Temporary Links — Home Assistant add-on

Generate **temporary links**: shareable, time-boxed, usage-limited URLs that trigger
Home Assistant actions (open a gate, unlock a door, run a scene) — deliverable by SMS
via Twilio or by copying the URL, with a full audit trail.

The product's specification lives in the **facit** ([`docs/facit/app/facit.json`](docs/facit/app/facit.json)) —
user stories + acceptance criteria as validated JSON. How we work: [`CLAUDE.md`](CLAUDE.md).

## How it works

1. The operator composes a link in the add-on dashboard (actions, validity window,
   usage allowance, optional recipient).
2. The add-on registers a webhook automation in HA and a **cloudhook** (public URL via
   HA Cloud / Nabu Casa). The automation never runs the link's actions itself — it only
   **announces** the press: inside the validity window it fires `temp_link_triggered`,
   outside it `temp_link_blocked` (so even refused attempts are audited).
3. On `temp_link_triggered` the add-on judges the link (unknown / exhausted / revoked /
   expired / not-yet-valid), **atomically claims a use**, and only then runs the actions —
   the allowance binds the actions, not just the bookkeeping. The automation is deleted
   (with retry) when the link dies, re-armed at startup if it predates the current model,
   and a press missed while the add-on was offline is counted and audited on reconnect —
   deliberately never executed late.

Requires: HA Cloud (Nabu Casa) for cloudhooks; a long-lived HA access token; optionally
Twilio for SMS delivery.

## Security posture

Read this before exposing anything.

- **The dashboard has no login of its own.** It is designed to be reached **only**
  through Home Assistant ingress (the add-on panel), which authenticates via HA.
  **Never expose port 8099 directly** (no port mapping, no reverse proxy to it): any
  request that reaches the app bypassing ingress is served unauthenticated. The app
  also trusts forwarded headers from any proxy — another reason it must stay behind
  ingress only.
- **The trigger URL is the credential.** Tokens are 32-character URL-safe secrets from
  a CSPRNG (~192 bits). Anyone holding the URL can use the link within its grant —
  treat sent links like keys, use short windows and small allowances, and revoke when
  in doubt.
- **Grant enforcement is layered:** the home decides in/out of window and announces every
  attempt (blocked presses fire a distinct event, so they are audited); the add-on
  atomically claims a use before running any action; revocation and exhaustion delete
  the automation (retried by the sweep until confirmed); startup re-arms any trigger
  that predates the current enforcement model.
- **Data at rest** (`/data/temporarylinks.db`, SQLite, unencrypted): tokens, phone
  numbers, cloudhook URLs, message content, audit history. It stays inside the add-on
  container's data volume; protect backups accordingly.
- **Secrets** (HA token, Twilio credentials) live in the add-on options (password-typed
  fields), never in the repo or the database.
- **Preview-bot immunity:** when the home has HA Cloud remote access, shared links point
  to a confirm page (hosted at `/local/` via the remote UI) and the trigger is POST-only,
  so chat-app preview bots can neither fire nor consume a link. The public URL is
  **auto-discovered from HA Cloud** — the `public_url` option is only an override for
  unusual setups (own domain/proxy). Without any public URL, links fall back to the
  direct one-tap form, which preview bots can consume.

## Install

Install through the add-on store, straight from this repository:

1. **Settings → Add-ons → Add-on Store → ⋮ (top right) → Repositories**, add
   `https://github.com/we4sz/ha-temporary-links`.
2. **Temporary Links** appears in the store; install it (HA builds the image locally —
   the first build takes a few minutes).
3. Configure options: `ha_url` (e.g. `http://homeassistant.local:8123`), `ha_token`
   (a long-lived access token), optionally Twilio credentials for SMS. `share_page_url`
   already defaults to the hosted confirm page, so links are bot-immune out of the box.
4. HA Cloud (Nabu Casa) must be connected — cloudhooks are what make links publicly
   reachable. Open the dashboard through the add-on's panel (ingress) only.

To update: the store follows this repo's `main` — **⋮ → Check for updates**, then
**Update** on the add-on's page (HA rebuilds locally).

## Development

```sh
dotnet build ha-temporary-links.sln
dotnet test tests/TemporaryLinks.Addon.Tests --logger trx --collect:"XPlat Code Coverage"
python3 tools/facit/facit.py validate     # the facit gate
python3 tools/facit/facit.py status       # spec coverage + proven ACs
```

Development loop: stories → gap → build ([`CLAUDE.md`](CLAUDE.md)). The facit wins conflicts.

### Integration tests (opt-in, needs Docker)

The default `dotnet test` gate never touches Home Assistant — the suite in
[`tests/TemporaryLinks.Addon.IntegrationTests/`](tests/TemporaryLinks.Addon.IntegrationTests/)
self-skips unless `HA_TEST_URL` is set. To prove the add-on ↔ HA seam (automation
accepted and loaded, validity window enforced by the home, confirm-page links POST-only,
action-picker feed and service execution real) against a throwaway real HA container:

```sh
tests/integration/run-ha-tests.sh
```

The one seam it cannot reach locally is Nabu Casa (cloudhook creation + the remote-UI
confirm page) — that still needs a one-time manual check on a cloud-connected instance.
