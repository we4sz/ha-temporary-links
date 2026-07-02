# Changelog

## 1.1.0 — 2026-07-02

### Fixed
- **Grant enforcement:** the validity window is now enforced by the Home Assistant automation
  itself (a template condition), so actions no longer run before a link's start time; links that
  expire at execution time now clean up their automation; failed creation no longer orphans a
  trigger; revoke survives a Home Assistant outage.
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
