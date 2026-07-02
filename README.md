# Instant File Share

Instant File Share is a Windows desktop app for sharing files and folders from your own machine with temporary web links.
It works by hosting a local HTTP server, then serving the selected file on a randomly generated route.

It supports Cloudflare proxies by default, allowing you to use your own domain instead of the default anonymous "trycloudflare" domain.

You can define usage restrictions for Share URLs, such as expiration time and max uses.

## What It Does

- Copy Share Link to a file.
- Copy Directory Share Link as a zip file.
- Copy Directory Share Link as a browsable page.
- Copy Receive Link to directory, allowing others with the link to upload files to that directory.

## How to use

The two main features are sending and receiving files.

You can send files by right-clicking one and selecting "Copy Share Link". Send that link for others to download the file.

You can receive files by right-clicking a directory and selecting "Receive Files Here". This will copy a Receive link to the clipboard, allowing clients to upload files to that directory.

The links expire once it reaches the limits you have defined in the settings.

## Settings

The Settings page saves changes to the local agent. Most settings become the defaults for new Share and Receive links. Links that already exist keep the token, publish mode, expiry, and usage limits they had when the app created them.

Some settings affect the running app as soon as you change them. The app updates Explorer context-menu entries, start-on-login registration, visible log sections, history cleanup, and transfer behavior from the saved settings. Changing the local API port, manual public port, or manual bind address schedules an agent restart because those values control the HTTP listeners.

### Sharing

- **Publish Mode** controls how new links are exposed.
  - **Quick Tunnel** starts a session-scoped Cloudflare tunnel with `cloudflared`.
  - **Custom Cloudflare Domain** uses your Cloudflare login, selected domain, and subdomain. Configured under the Cloudflare section.
  - **Manual** uses the public bind address, public port, and optional base URL you configure.
- **Public token length** controls the random part of generated URLs. The default is 11 characters.
- **Keep PC awake** prevents the machine from going into sleep mode while transfers are active. The machine can still sleep, it will just not interrupt on going transfers.
- **Start on login** Start Instant File Share on Windows startup.
- **Open Dashboard on start** opens the Dashboard window when Instant File Share starts.

### Sending Files

- **Expiry** sets the default lifetime for new file and folder shares. Use `0` to create links without an automatic expiry.
- **Max Uses** limits completed downloads for new shares. Leave it empty for unlimited uses.
- **If the shared file changes** decides whether a share should stop serving after the original file changes or keep serving the newest file at that path.
- **Friendly URLs** adds a readable filename or folder slug after the random token.
- **File and folder context-menu toggles** add or remove Explorer actions for sharing a file, sharing a folder as ZIP, and sharing a folder for browsing.
- **Browse page title** changes the public title shown on folder browsing pages. A blank value falls back to the Windows username-based default.
- **Folder share mode** decides whether a new folder share exposes only the selected mode or allows both browsing and ZIP download routes.
- **Folder ZIP compression** controls CPU-versus-size tradeoffs for streamed ZIP downloads.

### Receiving Files

- **Upload page title** changes the public title shown on Receive pages. A blank value falls back to the Windows username-based default.
- **Receive link expiry** sets the default lifetime for Receive links. Use `0` to keep them from expiring by time.
- **Max total upload per link** caps how much data one Receive link can accept. The Unlimited option stores `0`.
- **Parallel uploads** controls how many files a Receive page can upload at the same time. Use `0` for no limit.
- **Upload mode** chooses the browser upload transport. `Auto` probes the available methods, while the other modes force multipart chunks, binary chunks, WebSocket chunks, or the experimental compressed stream path.
- **Packet sizing** controls request sizing for large uploads. Fixed mode uses the configured packet size. Auto mode recommends packet sizes from observed speed and the target request time.
- **Max request body size** caps each upload request body. The default keeps requests below common Cloudflare Free/Pro body-size limits.
- **Receive notifications** shows a local notification when uploads finish.
- **Folder context menu: Receive files here** adds the Explorer action that creates Receive links for folders.

### Browser Transfer Behavior

- **Browser-managed downloads** uses the public browser page to manage file downloads so the app can retry chunks, pause, resume, verify, and fall back to direct download when needed.
- **Managed download memory limit** controls how large a file the browser can assemble in memory before it needs streaming save support or direct download fallback.
- **Managed download parallel chunks** limits how many chunks the browser can fetch at once.
- **Managed compression** lets browser-managed transfers compare raw and gzip paths for files that may compress well.
- **Browser transfer encryption** controls application-level encryption for browser-managed transfers. Raw direct downloads do not use this setting.
- **Open Images/Videos/PDF in Browser** lets supported files render inline. Turn these off to force downloads.
- **Allow rich embed** lets chat apps and link-preview crawlers receive metadata for better previews.

### Server, History, And Debug

- **Transfer Speed Limit** caps outgoing download speed. Leave it empty for full speed.
- **Manual mode bind address** chooses the local interface for the public listener, such as `0.0.0.0` for all interfaces or `127.0.0.1` for loopback only.
- **Manual mode public port** is the public share listener port. Manual internet access requires your router, firewall, or reverse proxy to expose this port.
- **Manual base URL override** is the public URL embedded in generated Manual-mode links. Use it when a reverse proxy or DNS name fronts the local listener.
- **Local API port** is the loopback control API port used by the dashboard and local integrations.
- **Keep history for** prunes completed transfer history after the selected age. Use `0` for unlimited retention.
- **Items per page** controls pagination in the Shares and History views. Use `0` to show all rows.
- **Show logs** adds agent and Cloudflare log views to the sidebar.
- **Download diagnostics** and **Upload diagnostics** show transfer-mode decisions, chunk sizing, retry details, and fallback reasons on public pages.

### Cloudflare

- **Cloudflared path override** points the agent at a specific `cloudflared.exe` instead of relying on auto-detection.
- **Install with winget** installs `cloudflared` when Winget is available.
- **Update cloudflared** updates only Winget-managed installs.
- **Start Cloudflare login / Log out** manages the local Cloudflare authentication used for custom-domain mode.
- **Domain** and **Subdomain** choose the hostname used by Custom Cloudflare Domain links.

## TO-DO

- Remote upload/server-hosted mode
- HTTPS certificate management for manual deployments

## Disclosure

AI tools were used while creating this project.

## License

Instant File Share is licensed under the MIT License. See [LICENSE](LICENSE).
