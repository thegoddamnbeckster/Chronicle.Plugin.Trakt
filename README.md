# Chronicle.Plugin.Trakt

Chronicle import plugin for [Trakt.tv](https://trakt.tv). Imports your watch history, ratings, and watchlist into Chronicle via the Trakt v2 API.

## Setup

1. Create a Trakt application at <https://trakt.tv/oauth/applications>.
2. In Chronicle → Plugins, install this plugin and set:
   - **Client ID** — your application's client ID
   - **Client Secret** — your application's client secret
3. Go to Chronicle → Settings → Import → Trakt and start the device auth flow.
4. Visit the displayed URL, enter the code, and Chronicle will automatically store your access token.

## Import

After authentication, use the Import page to sync:
- **Watch history** — all movies and episodes you've watched (supports incremental `since` parameter)
- **Ratings** — all your Trakt ratings (1–10 scale)
- **Watchlist** — all items on your watchlist (added as *Plan to Watch*)

## Rate limits

Trakt allows 1,000 API calls per 5-minute window. The plugin tracks the `X-RateLimit-Remaining` response header and automatically pauses when the window is exhausted to keep imports from being blocked.

## Building

```bash
dotnet build
dotnet publish -c Release -o dist/
```

Copy the contents of `dist/` (including `manifest.json`) into your Chronicle `plugins/` directory.
