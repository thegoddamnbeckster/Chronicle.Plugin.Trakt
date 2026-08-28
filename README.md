# Chronicle.Plugin.Trakt

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.Trakt?label=Chronicle.Plugin.Trakt&color=ed1c24)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Trakt/releases/latest)

Trakt.tv import and metadata plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Imports your watch history, ratings, and watchlist from [Trakt.tv](https://trakt.tv) via the Trakt v2 API. Also provides metadata (title, overview, poster, cast) for matched items so Chronicle can enrich them without needing a separate TMDB lookup.

**Plugin ID:** `chronicle.plugin.trakt`
**Version:** 1.1.0
**Implements:** `IImportProvider` + `IMetadataProvider`
**Auth:** Trakt Device Auth (OAuth2 — no password required)

---

## Supported Media Types

| Media Type | Import | Metadata |
|------------|--------|---------|
| `movies` | ✓ watch history, ratings, watchlist | ✓ title, overview, year, poster, cast, directors |
| `tv` (shows + episodes) | ✓ watch history, ratings, watchlist | ✓ title, overview, year, poster |

---

## External ID Format

`trakt:{type}:{id}` — for example:

- `trakt:movie:12345` → a Trakt movie
- `trakt:show:67890` → a Trakt TV show
- `trakt:episode:99999` → a Trakt episode

Fix Match accepts full Trakt URLs:
- `https://trakt.tv/movies/fight-club`
- `https://trakt.tv/shows/breaking-bad`
- `https://trakt.tv/shows/breaking-bad/seasons/1/episodes/1`

---

## Setup

### Step 1 — Create a Trakt application

> **Requires Trakt VIP.** As of 2026, Trakt gates creating a new API application behind a paid
> VIP membership ("Creating new apps requires Trakt VIP" on the applications page) — a free
> account cannot obtain a Client ID at all, and this plugin cannot work around that; it's a
> restriction on Trakt's own platform, not something Chronicle can bypass. If you don't already
> have a registered application from before this changed, you'll need Trakt VIP to create one.

1. Go to [trakt.tv/oauth/applications](https://trakt.tv/oauth/applications) and click **New Application**.
2. Give it a name (e.g. "Chronicle") and set the redirect URI to `urn:ietf:wg:oauth:2.0:oob`.
3. Note your **Client ID** and **Client Secret**.

### Step 2 — Install the plugin

1. In Chronicle → **Plugins**, find Trakt and click **Install**.
2. Go to **Settings** for the plugin and enter your **Client ID** and **Client Secret**.
3. Click **Save**.

### Step 3 — Authenticate

1. In Chronicle → **Plugins**, open Trakt's **Configure** panel and click **Connect Account**.
2. Chronicle will display a short code and a URL.
3. Visit [trakt.tv/activate](https://trakt.tv/activate), sign in, and enter the code.
4. Chronicle polls for confirmation and stores your access token automatically.

---

## Importing

After authentication, use the Background Tasks page to run:

| Task | What it does |
|------|-------------|
| **Import All** | Full import of your entire Trakt history, ratings, and watchlist. Run once after connecting. |
| **Delta Sync** | Imports only activity since the last sync. Runs automatically on schedule (default: daily 2:00 UTC). |

During import, Chronicle:
1. Looks up each Trakt item by its TMDB cross-reference ID (stored by Trakt on every item)
2. Creates or finds the matching Chronicle `MediaItem`
3. Records watch events, ratings, and library statuses without duplicating existing entries

---

## Rate Limiting

Trakt allows 1,000 API calls per 5-minute window — a rolling window that resets continuously,
not a daily cap.

HTTP 429 responses are handled reactively: the plugin honors the `Retry-After` header and
retries, bounded to 5 attempts per page (`MaxRetriesPerPage` in `TraktClient.cs`) before giving
up on that sync run — this replaced an earlier unbounded retry loop that could retry the same
page forever under sustained 429s. This is *not* proactive `X-RateLimit-Remaining` tracking with
a sleep-until-reset — the client doesn't read that header today; it only reacts to an actual 429.

---

## Repository Structure

```
Chronicle.Plugin.Trakt/
├── Chronicle.Plugin.Trakt.csproj
├── manifest.json
├── TraktPlugin.cs             # Entry point — registers IMetadataProvider + IImportProvider
├── TraktMetadataProvider.cs   # IMetadataProvider: search, get by ID, Fix Match
├── TraktClient.cs             # HTTP client, auth, rate limiting
└── Models/                    # API response models
```

---

## Building

```powershell
dotnet build -c Release
```

Deploy to Chronicle:

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.trakt"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\*.dll" $pluginDir
Copy-Item "manifest.json"           $pluginDir
```

> **Important:** `Chronicle.Plugins.dll` must **not** be in the plugin directory — Chronicle provides it. The `.csproj` sets `<Private>false</Private>` on the Chronicle.Plugins reference to ensure this.

---

## Development

Both repositories must be cloned as siblings:

```
<base>\
  Chronicle\
  Chronicle.Plugin.Trakt\
```

The plugin references `Chronicle.Plugins` via a local project reference:

```xml
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
```

---

## License

MIT
