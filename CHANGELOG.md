# Changelog

## 1.2.0 — 2026-08-16

### Changed
- **One way to share a link: the confirm page.** Every link is now handed out as the confirm
  page (the hosted one, or the copy the add-on serves from your home's public URL), and its
  trigger accepts only that page's button press. The direct one-tap URL is gone — with it goes
  the last way a chat app's link preview could quietly consume someone's access. Links created
  by an earlier version are re-armed to the new gesture at startup; until that lands (an
  unreachable home, say) such a link keeps handing out the URL its own trigger still accepts,
  so nothing breaks mid-flight.
- **Creating a link now requires a confirm page to share it with.** The shipped
  `share_page_url` default provides one, so nothing changes for a normal install. If you
  cleared that option *and* have no HA Cloud remote access (or `public_url`), creation is now
  refused with an explanation of what to enable, instead of silently falling back to a link a
  preview bot could fire.

## 1.1.4 — 2026-08-16

### Added
- **Template composer matches the link form:** creating or editing an action template now
  offers the same action picker link creation uses — pick a service and an entity from the
  home's actual registries and it's appended to the actions JSON, which stays hand-editable as
  a fallback. The picker is one shared component now, not two copies.

### Fixed
- **Templates are validated and normalized at save**, through the identical contract link
  creation enforces: `service`/top-level `entity_id` (the home's own automation syntax) are
  rewritten to the canonical form, and a form link creation would refuse (e.g. a stale 1.0.x
  device-action template) is refused at save with a clear explanation instead of becoming a
  landmine that only fails later, at link creation.

## 1.1.3 — 2026-08-16

### Added
- The confirm page shows its version discreetly (e.g. "Temporary Links · v1.1.3"), so a stale
  cached or outdated self-hosted copy is recognizable at a glance. A test pins the page's
  version to the release version — they cannot drift apart.

## 1.1.2 — 2026-08-16

### Fixed
- **Confirm page no longer downloads a file:** pressing Open now sends the trigger from the
  page itself and confirms inline ("Sent — if your link is valid, it has now run") instead of
  navigating the browser to the relay's empty reply, which downloaded as a file. A failed send
  says so and invites another press; re-pressing is safe (the home refuses anything beyond the
  link's grant).

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
