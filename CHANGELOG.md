# Changelog

## 1.1.1 — 2026-08-16

### Fixed
- **Pre-upgrade links are migrated:** on startup every active link's automation is re-armed to
  the current enforcement model, so links created by 1.0 (whose automations embedded their real
  actions) stop double-executing and start obeying the usage allowance and validity window.
- **Usage counting is race-proof end to end:** the tracked entity no longer writes a stale count
  back over the atomic claim; an exhausted-but-active link (e.g. after its allowance is edited
  down) is retired on the next trigger instead of lingering.
- **Presses during add-on downtime are reconciled:** on reconnect the add-on compares each
  automation's last-fired time with what it processed — a missed in-window press is counted and
  audited (actions are deliberately never run late), so no use is silently forgotten.
- **Out-of-window attempts are audited:** the automation now always reports the attempt (a
  blocked press fires a distinct event) while the home still refuses the use itself.
- **Share URLs always match the armed trigger:** the accepted gesture (one-tap GET vs confirm-page
  POST) is recorded per link, so a configuration change can no longer hand out links the trigger
  rejects with 405.
- Actions written in Home Assistant's own automation syntax (`service`, top-level `entity_id`)
  are normalized at creation instead of burning a use and failing at execution; invalid actions
  are refused at creation with a clear message.
- A failed trigger removal is retried by the sweep until it lands; a successful use is no longer
  reported as an error just because cleanup failed; a store failure during creation no longer
  orphans a live automation and cloudhook.
- The shipped container now runs with the tuned SQLite settings (busy timeout, no pooling); the
  confirm page survives truncated links; the action picker no longer discards a hand-edited
  draft it cannot parse.

### Added
- **Bot-immune links out of the box:** the shared confirm page is now hosted at
  `https://we4sz.github.io/ha-temporary-links/open.html` (deployed from `sharepage/` by CI) and
  ships as the default `share_page_url` — every install gets preview-bot-immune, POST-only links
  with zero configuration. The hook rides only in the URL fragment, so the host never sees it;
  clear the option or point it at your own copy if you prefer not to trust the shared host.

## 1.1.0 — 2026-07-02

### Fixed
- **Grant enforcement (usage limit):** a link's actions are now run by the add-on *after* it
  atomically claims a use, and the automation fires only a tracking event — so a one-time link
  can no longer be over-consumed by rapid re-taps or replays, and the count can never exceed the
  allowance. Trade-off: links now require the add-on to be running in order to fire.
- **Grant enforcement (window):** the validity window is enforced by the Home Assistant automation
  itself (a template condition), so actions no longer run before a link's start time; links that
  expire at execution time now clean up their automation; failed creation no longer orphans a
  trigger; revoke survives a Home Assistant outage.
- Tokens and cloudhook URLs are no longer written to the logs.
- SQLite is configured with a busy timeout so a contended write waits instead of dropping a use.
- Status filter on the links list now works (it was a no-op).
- SMS delivery records now reflect that the message was accepted.

### Added
- **Recipient-optional links** — create a link with no phone number and share the URL yourself.
- **Copy-link button** on the link details page.
- **Action picker** — compose actions by choosing from your home's services and entities instead
  of writing JSON (raw JSON remains available).
- **Preview-bot-immune links** — an opt-in confirm page (auto-discovered via HA Cloud, self-hosted,
  or a shared hosted page) makes the trigger POST-only so chat-app link previews can't fire it.
- Security posture documented in the README.

### Changed
- Dependencies updated (EF Core, Twilio) and a high-severity transitive SQLite advisory cleared.
