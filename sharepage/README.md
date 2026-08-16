# Shared confirm page

`open.html` is the bot-immune confirm page any Temporary Links installation can use —
one hosted copy serves everyone. The cloudhook URL rides **only in the `#fragment`**,
which browsers never send to the server: the host of this page never sees anyone's
trigger URL, and the page only ever POSTs to `https://hooks.nabu.casa/…` (no open
redirect). Keep it in sync with `SharePage.Html` in
`src/TemporaryLinks.Addon/Services/SharePage.cs` (a test asserts the invariants).

## The published copy

The canonical hosted copy is **https://we4sz.github.io/ha-temporary-links/open.html**,
deployed automatically by `.github/workflows/pages.yml` whenever `sharepage/` changes
(GitHub Pages, Actions source). Put that URL in the add-on's `share_page_url` option —
or ship it as the option's default in `config.yaml` so every install gets it with zero
configuration. Anyone who prefers their own host can copy `open.html` anywhere static.

Installations that prefer not to depend on a shared host can leave `share_page_url`
empty: the add-on then self-hosts the same page via HA Cloud remote access (`/local/`),
auto-discovered — or falls back to direct one-tap links with no confirm page at all.
