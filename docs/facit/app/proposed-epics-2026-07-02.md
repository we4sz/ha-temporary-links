# Proposed epics / stories / ACs — 2026-07-02

Derived from [audit-2026-07-02.md](audit-2026-07-02.md). **Proposal only** — not yet in
`facit.json`. Written in the facit's conceptual vocabulary (no type/file names) so adopted
items can drop straight into `docs/facit/app/facit.json`. Roles: **operator**, **recipient**,
**add-on**, **maintainer**.

Ordering reflects recommended priority: E7 (correctness/security of the core guarantee) first,
then E9 (operability), then E8/E10 (product reach).

---

## E7 — Trustworthy enforcement

*Framing:* A temporary link promises time-boxed **and usage-limited** access. Today the home
enforces only the window; the usage limit is counted after the fact by the add-on, so the real
action can fire more times than granted. This epic makes the grant a guarantee, not an estimate,
and closes the consistency gaps around it.

- **E7.S1 — As the add-on, I want a link's real actions to run at most as many times as its
  allowance, even under burst, replay, or while I am offline, so that a one-time link is truly
  one-time.**
  - A1: Given a link at its allowance, when its trigger is fired again, then the home does not
    run the link's actions — enforcement binds the actions, not just the recorded count.
  - A2: Given two triggers arrive within milliseconds on a single-use link, when both are
    processed, then the actions run at most once and exactly one use is recorded.
  - A3: Given the add-on was offline when a link was used, when it comes back, then the use is
    reconciled (counted and audited) and an exhausted link's trigger is retired — a use during
    downtime is never silently forgotten.
- **E7.S2 — As the add-on, I want to refuse to issue a link that cannot be enforced safely,
  so that there is no insecure default.**
  - A1: Given no bot-immune (POST + confirm-page) path is available, when a link would otherwise
    be shared as a bare GET-triggerable URL, then the operator is warned that the link is
    preview-bot-consumable before it is shared (fail loud, not silent).
- **E7.S3 — As the add-on, I want usage counting to be atomic and consistent, so that no race
  between triggers, edits, or the expiry sweep can miscount or clobber a link's state.**
  - A1: Given concurrent operations on one link, when they commit, then the usage count and
    status are updated atomically with no lost update (a conflict is detected and re-resolved).
  - A2: Given the store is briefly contended, when a counting write meets contention, then it
    retries rather than dropping the use.
- **E7.S4 — As the operator, I want secrets and capability URLs kept out of the logs, so that
  reading the logs never hands someone a working key.**
  - A1: Given any log level, when the add-on logs, then link tokens and cloudhook/trigger URLs
    are never written in the clear (omitted or redacted/hashed).
- **E7.S5 — As the operator, I want the shared confirm-page host treated as fully trusted code,
  so that I understand and consent to what hosting it can see.**
  - A1: Given a shared confirm-page host is configured, when the operator sets it, then the UI
    and docs state plainly that this host runs code with access to every shared link's trigger
    URL, and recommend a first-party/self-hosted origin.
- **E7.S6 — As the operator, I want a link's actions constrained to an intended, least-privilege
  set, so that a link cannot do more to my home than the access I meant to grant.**
  - A1: Given a link's actions, when it is created, then only actions within an allowed set of
    domains/services are accepted (a link cannot smuggle in arbitrary automation behaviour).
  - A2: Given the add-on talks to the home, when it authenticates, then it uses the least
    privilege sufficient for its function rather than a full-admin credential.

## E8 — The recipient's experience & operator awareness

*Framing:* The recipient is a non-technical outsider whose entire interaction is one link, and
the operator is granting physical access to their home. Both deserve to know what happened.

- **E8.S1 — As a recipient, I want a clear result after I tap my link (opened / expired /
  already used / not yet valid), so that I know whether I have access and what to do if not.**
  - A1: Given a link is used successfully, when the recipient confirms, then they see a plain
    success confirmation.
  - A2: Given a link is refused (expired, used up, revoked, not yet valid), when the recipient
    opens it, then they see a specific, non-technical explanation rather than a dead end or a raw
    error.
- **E8.S2 — As an operator, I want to be notified in Home Assistant when a link is used, refused,
  or auto-expires, so that I have real-time awareness of access to my home without watching a
  dashboard.**
  - A1: Given a link is successfully used, when the use completes, then an operator notification
    is emitted (through the home's own notification mechanism), configurable on/off.
  - A2: Given a link is refused, when the refusal is recorded, then an abuse/awareness
    notification can be emitted for out-of-grant attempts.
- **E8.S3 — As a recipient and operator, I want link text and recipient-facing pages available in
  my language, so that the product works for non-English households.**
  - A1: Given the add-on's configured language, when operator-facing UI and recipient-facing
    pages render, then their fixed text is localisable (not hard-coded English).

## E9 — Operability & safe delivery

*Framing:* The add-on is an appliance a stranger installs and forgets. It must build on the
hardware it advertises, survive restarts and flaky networks, fail in ways the operator can act
on, and never lose a link's history to an upgrade.

- **E9.S1 — As an operator, I want the dashboard protected even if it is reached outside the
  home's authenticated panel, so that a misconfiguration does not expose control of my home.**
  - A1: Given a request that did not arrive through the home's authenticated ingress, when it
    reaches the dashboard, then it is not served with full control (defence in depth beyond
    "don't expose the port").
  - A2: Given forwarded request metadata, when the add-on records who acted, then it does not
    trust spoofable headers for that record.
- **E9.S2 — As a maintainer, I want the advertised hardware architectures to match what actually
  builds, so that installs never fail on unsupported hardware.**
  - A1: Given the set of advertised architectures, when the add-on image is built, then every
    advertised architecture produces a working image (or is not advertised).
- **E9.S3 — As an operator, I want the event listener to detect a dead or unauthorised
  connection and surface it, so that link usage never silently stops being tracked.**
  - A1: Given a stalled or half-open connection to the home, when no events arrive within a
    liveness interval, then the add-on forces a reconnect.
  - A2: Given the home rejects the add-on's credentials, when authentication fails repeatedly,
    then it is surfaced as a distinct, actionable state and backs off — not an infinite silent
    retry.
- **E9.S4 — As an operator, I want a messaging-provider outage to disable only messaging, so that
  an unrelated third party cannot take the whole add-on down.**
  - A1: Given the provider is unreachable or invalid at startup, when the add-on starts, then it
    starts with messaging disabled and a clear warning, rather than refusing to run.
- **E9.S5 — As a maintainer, I want upgrades to protect existing link history, so that a failed
  schema migration is recoverable without data loss.**
  - A1: Given a pending schema migration, when the add-on starts, then the data store is backed
    up first and a migration failure is reported clearly without crash-looping into an
    unrecoverable state.
- **E9.S6 — As an operator, I want the home to detect and restart a wedged add-on, so that a
  silently-dead process does not masquerade as healthy.**
  - A1: Given the add-on is running, when its health is checked, then health reflects data-store
    reachability and event-listener liveness, and the home is configured to watch it.
- **E9.S7 — As a maintainer, I want every change gated by automated checks, so that regressions
  and un-buildable architectures never ship.**
  - A1: Given a proposed change, when it is submitted, then the test suite, the facit gate, and a
    build for each advertised architecture all run automatically.
- **E9.S8 — As an operator, I want configuration to be simple and singular, so that setup has no
  redundant or dead paths.**
  - A1: Given a standard install, when the add-on starts, then it uses the home's built-in
    supervisor access by default, with a manual URL/token only as an override, via one
    documented configuration path.

## E10 — Scheduling & reuse at scale

*Framing:* Real access needs are recurring and plural: the weekday dog-walker, the cleaning crew,
the standing grandma button. Today every grant is a single one-shot window to one number.

- **E10.S1 — As an operator, I want to grant access on a recurring schedule (days of week + a
  daily time window, with an optional end date), so that regular helpers get standing access
  without me re-issuing links.**
  - A1: Given a recurring schedule, when a trigger arrives, then the link executes only within an
    active occurrence of the schedule and is refused otherwise.
  - A2: Given a recurring link, when its allowance is expressed per occurrence (e.g. once per
    day), then use is limited within each occurrence rather than as a single lifetime total.
- **E10.S2 — As an operator, I want to grant one link to several recipients at once, so that
  shared-access scenarios don't require hand-managing duplicate links.**
  - A1: Given several recipients, when the operator sends a link, then it is delivered to all of
    them in one action, with per-recipient delivery recorded.
- **E10.S3 — As an operator, I want a scannable code for a link, so that I can grant access in
  person or on paper without messaging or copy-paste.**
  - A1: Given a link, when the operator views its details, then a scannable code for its share
    URL is available.
- **E10.S4 — As an operator, I want to find and review links and history as they accumulate, so
  that the dashboard stays usable over months of use.**
  - A1: Given many links, when the operator searches/filters (by name, recipient, date, status)
    and pages, then they can locate any past grant.
  - A2: Given a long audit history, when the operator views it, then older events remain reachable
    (paging/filtering/date range), not truncated to the newest few.
  - A3: Given the operator wants an off-app record, when they export, then link and audit history
    can be exported.
- **E10.S5 — As an operator, I want each link attributed to the household member who created it,
  and times shown with an explicit timezone, so that "who granted this, and when" is
  unambiguous.**
  - A1: Given a link is created through the authenticated panel, when creation is recorded, then
    the acting household member is attributed (not a generic label).
  - A2: Given any time is shown or sent, when it is rendered, then its timezone is explicit and
    consistent between what the operator sets, what the home enforces, and what the recipient
    reads.

---

## Decisions (2026-07-02)

- **E7.S1 enforcement mechanism → add-on in the request path.** The webhook automation will
  fire only the tracking event (no actions); the add-on atomically checks and decrements the
  usage count and *then* calls the Home Assistant service to run the actions; an exhausted link
  is refused and its automation deleted. Accepted trade-off: the add-on must be running for any
  link to fire, and actions run a beat later. **Not yet built** — the operator chose "spec only"
  for now.
- **E9.S2 (arch) — done:** advertised architectures trimmed to amd64/aarch64/armv7.
- **E7 enforcement cluster — BUILT (2026-07-02):** E7.S1 (actions run by the add-on after an
  atomic claim; automation fires only the event), E7.S3 (atomic conditional-UPDATE claim +
  SQLite busy timeout), and E7.S4 (tokens/URLs out of the logs). 34 ACs now proven; the gap has
  no `contradicted` items left. Remaining E7: S2 (fail-closed warning), S5 (shared-host trust
  doc), S6 (action allowlist + least-privilege token).
