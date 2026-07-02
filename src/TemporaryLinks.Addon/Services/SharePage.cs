namespace TemporaryLinks.Addon.Services;

/// <summary>
/// The bot-immune confirm page. The trigger webhook only accepts POST; this static page
/// carries the cloudhook URL in the location fragment (never sent to any server, invisible
/// to link-preview bots) and fires it with an explicit form POST — one human gesture that
/// no preview bot performs. Served unauthenticated from Home Assistant's /local/ static dir.
/// </summary>
public static class SharePage
{
    public const string RelativePath = "temporary_links/open.html";

    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex, nofollow">
            <title>Temporary access link</title>
            <style>
                body { font-family: -apple-system, system-ui, sans-serif; display: flex;
                       align-items: center; justify-content: center; min-height: 100vh;
                       margin: 0; background: #f4f5f7; }
                .card { background: #fff; border-radius: 12px; padding: 2rem;
                        box-shadow: 0 2px 12px rgba(0,0,0,.08); text-align: center;
                        max-width: 22rem; }
                button { font-size: 1.2rem; padding: .8rem 2.4rem; border: 0;
                         border-radius: 8px; background: #03a9f4; color: #fff;
                         cursor: pointer; }
                button:active { background: #0288d1; }
                .error { color: #b00020; }
            </style>
        </head>
        <body>
        <div class="card">
            <h2>Temporary access</h2>
            <p id="hint">Press the button to use your access link.</p>
            <form id="openForm" method="post">
                <button type="submit" id="openBtn">Open</button>
            </form>
        </div>
        <script>
            (function () {
                var hook = decodeURIComponent((location.hash || '').slice(1));
                var form = document.getElementById('openForm');
                // Only ever POST to a Home Assistant webhook relay — this page must not
                // be usable as an open redirector.
                if (hook.indexOf('https://hooks.nabu.casa/') === 0) {
                    form.action = hook;
                } else {
                    form.style.display = 'none';
                    document.getElementById('hint').textContent =
                        'This link is incomplete — ask the sender to share it again.';
                    document.getElementById('hint').className = 'error';
                }
            })();
        </script>
        </body>
        </html>
        """;

    /// <summary>
    /// Writes the confirm page into HA's static www directory. Tries the modern
    /// /homeassistant mount first, then the legacy /config mount. Returns the path
    /// written, or null when no www-capable mount is available.
    /// </summary>
    public static string? TryWrite(ILogger logger)
    {
        foreach (var root in new[] { "/homeassistant", "/config" })
        {
            if (!Directory.Exists(root))
                continue;
            try
            {
                var dir = Path.Combine(root, "www", "temporary_links");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "open.html");
                File.WriteAllText(path, Html);
                logger.LogInformation("Share confirm page written to {Path}", path);
                return path;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not write share confirm page under {Root}", root);
            }
        }
        return null;
    }
}
