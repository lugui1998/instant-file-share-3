# Instant File Share API

This document describes the API exposed by the Instant File Share agent in this repository.

## Listeners

The agent owns two HTTP listeners:

| Listener | Default bind/base URL | Purpose |
| --- | --- | --- |
| Local control API | `http://127.0.0.1:46430` | Desktop UI, runtime WebSocket, local settings, shares, transfers, and Cloudflare actions |
| Public share API | binds to `0.0.0.0:46431`; share URLs resolve from the selected publish mode | Download pages, file downloads, folder browsing, ZIP downloads, receive pages, and receive uploads |

`/api/*` and `/ws/*` only work on the local control listener. The agent returns `403 Forbidden` when a client calls those routes through the public listener.

In release builds, CORS accepts origins whose host is `127.0.0.1` or `localhost`. Development builds allow any origin.

## Serialization

The HTTP API uses JSON with camel-case property names. Enum values serialize as strings, for example `"Manual"` or `"FileDownload"`.

Date and time fields use ISO 8601 strings with UTC semantics when the field name ends in `Utc`.

## Common Models

### ShareRecord

```ts
type ShareRecord = {
  id: string
  token: string
  filePath: string
  fileName: string
  slug?: string | null
  publicBaseUrl: string
  fileSize: number
  fileModifiedAtUtc: string
  shareKind: "File" | "Folder"
  canBrowseFolderContents: boolean
  canDownloadFolderAsZip: boolean
  primaryFolderEntryPoint?: "Browse" | "Zip" | null
  createdAtUtc: string
  expiresAtUtc?: string | null
  maxUses?: number | null
  useCount: number
  publishMode: "QuickTunnel" | "ManagedCloudflare" | "Manual"
  state: "Active" | "Revoked" | "Expired" | "Broken"
  brokenReason?: string | null
  lastAccessedAtUtc?: string | null
}
```

### AppSettings

```ts
type AppSettings = {
  defaultPublishMode: "QuickTunnel" | "ManagedCloudflare" | "Manual"
  publicTokenLength: number
  defaultExpiryValue: number
  defaultExpiryUnit: "Minutes" | "Hours" | "Days"
  defaultMaxUses?: number | null
  defaultReceiveExpiryValue: number
  defaultReceiveExpiryUnit: "Minutes" | "Hours" | "Days"
  defaultReceiveMaxTotalBytes: number
  receivePageTitle: string
  friendlyUrlsEnabled: boolean
  sendMetadataToCrawlers: boolean
  openImagesInBrowser: boolean
  openVideosInBrowser: boolean
  openPdfInBrowser: boolean
  fileChangeBehavior: "Strict" | "Lenient"
  keepAwakeWhileTransferring: boolean
  bandwidthLimitBytesPerSecond?: number | null
  cloudflaredPathOverride?: string | null
  startOnLogin: boolean
  openDashboardOnStart: boolean
  manualBindAddress: string
  manualPublicPort: number
  manualBaseUrl?: string | null
  localApiPort: number
  showLogs: boolean
  addFileContextMenuButton: boolean
  addFolderZipContextMenuButton: boolean
  addFolderBrowseContextMenuButton: boolean
  addFolderReceiveContextMenuButton: boolean
  receiveNotificationsEnabled: boolean
  folderShareCapabilityPolicy: "Exclusive" | "AllowBoth"
  folderZipCompressionLevel: "Optimal" | "Fastest" | "NoCompression" | "SmallestSize"
  historyRetentionValue: number
  historyRetentionUnit: "Minutes" | "Hours" | "Days" | "Months" | "Years"
  historyItemsPerPage: number
  sharesItemsPerPage: number
}
```

### TransferSnapshot

```ts
type TransferSnapshot = {
  id: string
  shareId: string
  token: string
  fileName: string
  transferKind: "FileDownload" | "FolderZipDownload" | "FolderFileDownload" | "MetadataPreview" | "FileUpload"
  requesterName?: string | null
  clientSessionId?: string | null
  clientFingerprint?: string | null
  remoteAddress?: string | null
  bytesSent: number
  totalBytes: number
  progressBytes: number
  progressTotalBytes: number
  startedAtUtc: string
  lastUpdatedAtUtc: string
  completedAtUtc?: string | null
  state: "InProgress" | "Paused" | "Completed" | "Failed"
  isActive: boolean
  succeeded: boolean
  error?: string | null
}
```

`bytesSent` and `totalBytes` describe response bytes when the final size is known. Streamed ZIP downloads do not know their final compressed size ahead of time, so `progressBytes` and `progressTotalBytes` expose an estimated progress basis from source bytes compressed into the ZIP archive.

### CloudflaredState

```ts
type CloudflaredState = {
  executablePath?: string | null
  version?: string | null
  ownership: "Unknown" | "External" | "Winget"
  lastCheckedAtUtc?: string | null
  quickTunnelUrl?: string | null
  managedTunnelRunning: boolean
  activeMode?: "QuickTunnel" | "ManagedCloudflare" | "Manual" | null
}
```

## Health Endpoint

### `GET /`

Returns a basic agent health payload.

Available on both HTTP listeners.

Response:

```json
{
  "name": "Instant File Share Agent",
  "status": "ok"
}
```

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | The agent process accepted the request |

## Local Control API

Use the local control API through `http://127.0.0.1:{localApiPort}`. The default port is `46430`.

### `GET /api/runtime`

Returns the complete runtime snapshot used by the desktop UI.

Response:

```ts
type RuntimeSnapshot = {
  shares: ShareRecord[]
  transfers: TransferSnapshot[]
  settings: AppSettings
  cloudflared: CloudflaredState
}
```

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Runtime snapshot returned |
| `403 Forbidden` | The route was called from the public listener |

### `GET /api/shares`

Lists persisted shares.

Response: `ShareRecord[]`

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Shares returned |

### `POST /api/shares`

Creates or reuses a share for a file or folder.

Request:

```ts
type CreateShareRequest = {
  filePath: string
  publishMode?: "QuickTunnel" | "ManagedCloudflare" | "Manual" | null
  expiresAtUtc?: string | null
  maxUses?: number | null
  shareKind?: "File" | "Folder"
  primaryFolderEntryPoint?: "Browse" | "Zip" | null
}
```

Defaults:

| Field | Default |
| --- | --- |
| `publishMode` | `settings.defaultPublishMode` |
| `expiresAtUtc` | Computed from `settings.defaultExpiryValue` and `settings.defaultExpiryUnit`, unless expiry is disabled |
| `maxUses` | `settings.defaultMaxUses` |
| `shareKind` | `"File"` |

Response:

```ts
type CreateShareResponse = {
  share: ShareRecord
  url: string
}
```

Behavior:

- File shares point at the live file path, not a snapshot.
- Folder shares reject junctions, symlinks, and other reparse points.
- If an active compatible share already exists for the same path, mode, base URL, and folder capabilities, the agent returns the existing share.
- The returned `url` is built from the resolved public base URL and may include a friendly slug.

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Share created or reused |
| `500 Internal Server Error` | The target path is invalid, the publish mode cannot provide a public base URL, or another unhandled creation error occurs |

### `DELETE /api/shares/{shareId}`

Revokes a share. This endpoint is idempotent for missing share IDs.

Response: empty body.

Status codes:

| Status | Meaning |
| --- | --- |
| `204 No Content` | Revoke completed or the share did not exist |

### `POST /api/shares/{shareId}/show-in-explorer`

Opens the shared file's containing folder or the shared folder in Explorer.

Response: empty body.

Status codes:

| Status | Meaning |
| --- | --- |
| `204 No Content` | Explorer launch requested |
| `400 Bad Request` | The share path cannot be resolved to a directory |
| `404 Not Found` | The share ID does not exist |

### `GET /api/settings`

Returns saved settings.

Response: `AppSettings`

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Settings returned |

### `PUT /api/settings`

Replaces saved settings.

Request:

```ts
type UpdateSettingsRequest = {
  settings: AppSettings
}
```

Behavior:

- The agent normalizes token length, receive quota, receive page title, retention values, and pagination values.
- The agent updates startup registration and Explorer context-menu registration.
- Changing listener settings schedules an agent restart.
- Successful saves publish a `SettingsUpdated` runtime event.

Response: empty body.

Status codes:

| Status | Meaning |
| --- | --- |
| `204 No Content` | Settings saved |

### `GET /api/publish-profiles`

Returns publish profiles.

Response:

```ts
type PublishProfile = {
  mode: "QuickTunnel" | "ManagedCloudflare" | "Manual"
  baseUrl?: string | null
  bindAddress?: string | null
  publicPort: number
  cloudflareTunnelName?: string | null
  cloudflareHostname?: string | null
  cloudflareConfigPath?: string | null
  cloudflareToken?: string | null
  enabled: boolean
}
```

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Profiles returned |

### `PUT /api/publish-profiles/{mode}`

Saves the profile for one publish mode. The route value overwrites the request body's `mode`.

Path parameters:

| Name | Values |
| --- | --- |
| `mode` | `QuickTunnel`, `ManagedCloudflare`, `Manual` |

Request: `PublishProfile`

Response: empty body.

Status codes:

| Status | Meaning |
| --- | --- |
| `204 No Content` | Profile saved |
| `400 Bad Request` | The route `mode` is not a valid publish mode |

### `GET /api/transfers`

Lists active transfers followed by completed transfer history, ordered by newest `startedAtUtc`.

Response: `TransferSnapshot[]`

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Transfers returned |

### `DELETE /api/transfers/{transferId}`

Removes one completed transfer from history.

Response: empty body.

Status codes:

| Status | Meaning |
| --- | --- |
| `204 No Content` | Transfer removed or the transfer ID did not exist |
| `400 Bad Request` | The transfer is active and cannot be removed |

### `DELETE /api/transfers`

Clears completed transfer history and keeps active transfers.

Response: empty body.

Status codes:

| Status | Meaning |
| --- | --- |
| `204 No Content` | Completed history cleared |

### `GET /api/logs/agent`

Returns the agent log file as plain text.

Response content type: `text/plain`

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Log text returned |

### `GET /api/logs/cloudflare`

Returns the Cloudflare log file as plain text.

Response content type: `text/plain`

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Log text returned |

### `POST /api/cloudflared/detect`

Detects `cloudflared` using the configured override path first, then `PATH`. Saves the detected state and publishes `CloudflaredUpdated`.

Response:

```ts
type CloudflaredDetectionResult = {
  found: boolean
  path?: string | null
  version?: string | null
  ownership: "Unknown" | "External" | "Winget"
  message: string
}
```

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Detection completed |

### `POST /api/cloudflared/install`

Installs `cloudflared` through winget and refreshes detection after a successful install.

Response:

```ts
type CloudflaredActionResult = {
  success: boolean
  message: string
}
```

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Install attempt completed; inspect `success` |

### `POST /api/cloudflared/update`

Updates `cloudflared`. Automatic updates only work for supported ownership modes, currently winget-managed installs.

Response: `CloudflaredActionResult`

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Update attempt completed; inspect `success` |

### `POST /api/cloudflared/login`

Starts the external Cloudflare login flow through `cloudflared`.

Response: `CloudflaredActionResult`

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Login launch attempt completed; inspect `success` |

### `POST /api/cloudflared/logout`

Deletes the local Cloudflare certificate file from the current user's `.cloudflared` directory. Existing managed tunnel settings remain saved.

Response: `CloudflaredActionResult`

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Logout action completed |

### `GET /api/cloudflared/status`

Returns dashboard status for `cloudflared`, including install state, version comparison, ownership, and login state.

Response:

```ts
type CloudflaredDashboardStatus = {
  installed: boolean
  executablePath?: string | null
  installedVersion?: string | null
  latestVersion?: string | null
  updateAvailable: boolean
  ownership: "Unknown" | "External" | "Winget"
  loggedIn: boolean
  loginMessage: string
}
```

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Status returned |

### `GET /api/cloudflared/managed-status`

Returns managed Cloudflare login and configured tunnel status.

Response:

```ts
type CloudflareDomainOption = {
  zoneId: string
  name: string
}

type CloudflareManagedStatus = {
  loggedIn: boolean
  message: string
  accountId?: string | null
  zoneId?: string | null
  zoneName?: string | null
  domains?: CloudflareDomainOption[] | null
  configuredHostname?: string | null
  configuredTunnelName?: string | null
}
```

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Managed status returned |

### `POST /api/cloudflared/managed-check`

Checks whether the requested managed tunnel name and hostname already exist.

Request:

```ts
type CheckManagedTunnelRequest = {
  domain: string
  subdomain: string
}
```

Response:

```ts
type CloudflareManagedAvailability = {
  domain: string
  subdomain: string
  hostname: string
  tunnelName: string
  tunnelExists: boolean
  hostnameExists: boolean
  message: string
}
```

Behavior:

- The agent trims dots from `domain`.
- The agent sanitizes `subdomain`.
- The derived hostname is `{subdomain}.{domain}`.
- The derived tunnel name is `ifs-{subdomain}-{domain-with-dashes}` in lowercase.

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Availability check completed |

### `POST /api/cloudflared/managed-tunnel`

Creates or replaces the managed tunnel profile for a domain and subdomain.

Request:

```ts
type CreateManagedTunnelRequest = {
  domain: string
  subdomain: string
}
```

Response:

```ts
type ManagedTunnelProvisionResult = {
  success: boolean
  message: string
  hostname?: string | null
  tunnelName?: string | null
}
```

Behavior:

- Requires `cloudflared` detection to succeed.
- Requires a Cloudflare login authorized for the selected domain.
- Replaces a previously configured managed tunnel when the tunnel name changed.
- Saves the managed publish profile with `baseUrl` set to `https://{hostname}`.

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Provision attempt completed; inspect `success` |

## Runtime WebSocket

### `GET /ws/runtime`

Opens a WebSocket stream of runtime events.

Client URL:

```text
ws://127.0.0.1:46430/ws/runtime
```

The endpoint returns `400 Bad Request` when the request is not a WebSocket upgrade.

Message shape:

```ts
type RuntimeEvent = {
  type:
    | "ShareCreated"
    | "ShareUpdated"
    | "ShareRevoked"
    | "TransferStarted"
    | "TransferCompleted"
    | "TransferFailed"
    | "SettingsUpdated"
    | "CloudflaredUpdated"
    | "TransferProgress"
    | "TransferPaused"
    | "TransferRemoved"
    | "TransferHistoryCleared"
  occurredAtUtc: string
  payload: unknown
}
```

Payloads:

| Event type | Payload |
| --- | --- |
| `ShareCreated` | `ShareRecord` |
| `ShareUpdated` | `ShareRecord` |
| `ShareRevoked` | `ShareRecord` |
| `TransferStarted` | `TransferSnapshot` |
| `TransferProgress` | `TransferSnapshot` |
| `TransferCompleted` | `TransferSnapshot` |
| `TransferPaused` | `TransferSnapshot` |
| `TransferFailed` | `TransferSnapshot` |
| `TransferRemoved` | `{ transferId: string }` |
| `TransferHistoryCleared` | `{}` |
| `SettingsUpdated` | Usually `AppSettings`; managed tunnel provisioning currently sends the hostname string |
| `CloudflaredUpdated` | `CloudflaredState` |

## Public Share API

Use the public API through the selected share base URL. In manual mode, the listener binds to `0.0.0.0:46431` by default and generated URLs prefer the configured manual base URL, then the detected public IP, then the detected LAN IP.

### Public File Response Rules

File responses use `Content-Disposition` and `Content-Type`.

The agent serves these known types inline when the corresponding setting allows browser preview:

| Category | Extensions |
| --- | --- |
| Images | `.avif`, `.bmp`, `.gif`, `.heic`, `.heif`, `.jpeg`, `.jfif`, `.jpg`, `.png`, `.webp` |
| Videos | `.m4v`, `.mov`, `.mp4`, `.ogv`, `.webm` |
| PDF | `.pdf` |

Unknown types use `application/octet-stream` and `attachment`.

File and folder child downloads support HTTP range requests. Range downloads can return `206 Partial Content`.

### `GET /s/{token}`

Downloads a file share, redirects a folder share to its friendly folder URL, or serves crawler metadata.

Behavior:

- For file shares, standard clients receive the file body.
- For file shares, known metadata crawlers can receive an HTML metadata shell when `sendMetadataToCrawlers` is enabled.
- For folder shares with a slug, the agent redirects to `/s/{token}/{slug}`.
- Expired, revoked, exhausted, or broken shares return unavailable responses.

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | File body or metadata HTML returned |
| `206 Partial Content` | Range response returned |
| `302 Found` | Folder share redirected to friendly folder URL |
| `404 Not Found` | Token does not exist or route does not match the share |
| `410 Gone` | Share is revoked, expired, broken, or exhausted |
| `500 Internal Server Error` | Public share HTML rendering failed or a file streaming error reached the host |

### `HEAD /s/{token}`

Returns headers for the same resource as `GET /s/{token}` without a response body.

Status codes match `GET /s/{token}` where applicable.

### `GET /s/{token}/{slug}`

Serves a file share through its friendly slug or serves a folder browsing page.

Behavior:

- File shares allow a friendly slug segment. The slug does not change the resolved share token.
- Folder shares require the URL slug to match the share slug.
- Folder browse pages return an HTML shell with serialized page data.
- Folder shares return `404 Not Found` when browsing is disabled.

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | File, metadata HTML, or folder browse HTML returned |
| `206 Partial Content` | Range response returned for file content |
| `404 Not Found` | Slug mismatch, disabled folder browsing, missing path, or invalid traversal path |
| `410 Gone` | Share unavailable |

### `HEAD /s/{token}/{slug}`

Returns headers for the same resource as `GET /s/{token}/{slug}` without a response body.

Status codes match `GET /s/{token}/{slug}` where applicable.

### `GET /s/{token}/{slug}/{path}`

Serves a nested folder entry.

Path parameters:

| Name | Meaning |
| --- | --- |
| `token` | Share token |
| `slug` | Folder share slug |
| `path` | Folder-relative path using URL path segments |

Behavior:

- A directory path returns a folder browsing HTML page.
- A file path returns the file body with range support.
- Path traversal attempts, encoded traversal, backslash injection, absolute drive paths, UNC paths, and malformed encodings return `404 Not Found` or `400 Bad Request`.

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Folder HTML or file body returned |
| `206 Partial Content` | Range response returned for file content |
| `400 Bad Request` | Malformed raw path rejected by the host |
| `404 Not Found` | Entry missing or traversal rejected |
| `410 Gone` | Share unavailable |

### `HEAD /s/{token}/{slug}/{path}`

Returns headers for the same resource as `GET /s/{token}/{slug}/{path}` without a response body.

Status codes match `GET /s/{token}/{slug}/{path}` where applicable.

### `GET /s/{token}/{slug}.zip`

Downloads a folder share as a ZIP file when ZIP download is enabled.

Behavior:

- Standard clients receive `application/zip` with attachment disposition.
- Known metadata crawlers can receive an HTML metadata shell when `sendMetadataToCrawlers` is enabled.

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | ZIP stream or crawler metadata HTML returned |
| `404 Not Found` | ZIP download disabled, slug mismatch, or token missing |
| `410 Gone` | Share unavailable |

### `HEAD /s/{token}/{slug}.zip`

Returns ZIP headers for standard clients or metadata HTML headers for crawlers without a response body.

Status codes match `GET /s/{token}/{slug}.zip` where applicable.

### `GET /s/{token}/{slug}/{path}?download=zip`

Downloads the current folder path as a ZIP file when ZIP download is enabled.

Behavior:

- The ZIP contains the current directory's contents, not a parent folder wrapper.
- The route only works when `{path}` resolves to a directory.

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | ZIP stream or crawler metadata HTML returned |
| `404 Not Found` | ZIP download disabled, path is not a directory, entry missing, or traversal rejected |
| `410 Gone` | Share unavailable |

### `HEAD /s/{token}/{slug}/{path}?download=zip`

Returns headers for the current-folder ZIP response without a response body.

Status codes match `GET /s/{token}/{slug}/{path}?download=zip` where applicable.

### `GET /r/{token}`

Serves a receive-link upload page.

Response content type: `text/html; charset=utf-8`

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Receive page returned |
| `404 Not Found` | Token does not exist |
| `410 Gone` | Receive link is revoked, expired, exhausted, or broken |
| `500 Internal Server Error` | Public share HTML rendering failed |

### `HEAD /r/{token}`

Returns receive-page headers without a response body.

Status codes match `GET /r/{token}`.

### `POST /r/{token}`

Uploads files to a receive link.

Request content type: `multipart/form-data`

Form fields:

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `files` | file | yes | One or more uploaded files |
| `relativePaths` | string | no | Optional folder-relative path for the file at the same index in `files` |

Response:

```ts
type PublicReceiveUploadResponse = {
  uploadedCount: number
  failedCount: number
  remainingQuotaBytes: number
  results: Array<{
    fileName: string
    relativePath: string
    storedRelativePath?: string | null
    success: boolean
    message?: string | null
    sizeBytes: number
  }>
}
```

Behavior:

- Files write into the receive link target directory.
- The planner rejects traversal paths and paths through symlinked directories.
- When a top-level uploaded folder conflicts with an existing folder, the agent renames the incoming root folder once, for example `Photos (1)`.
- The receive link quota counts accepted bytes.
- Rejected files appear in `results` and do not necessarily make the batch fail.
- Successful uploads create `FileUpload` transfer records.

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Upload batch processed; inspect `uploadedCount`, `failedCount`, and `results` |
| `400 Bad Request` | Multipart parsing failed or no files were provided |
| `404 Not Found` | Token does not exist |
| `410 Gone` | Receive link unavailable |

### `GET /public-share-assets/{assetPath}`

Serves built public-share UI assets from the configured public-share assets directory.

Behavior:

- The asset locator must resolve the requested path under the configured assets directory.
- Unknown content types use `application/octet-stream`.

Status codes:

| Status | Meaning |
| --- | --- |
| `200 OK` | Asset returned |
| `404 Not Found` | Asset path is empty or cannot be resolved |

## Named Pipe API

Explorer integration sends JSON commands to the local named pipe:

```text
\\.\pipe\InstantFileShare.Agent
```

Request:

```ts
type PipeCommand = {
  command: string
  filePath?: string | null
}
```

Response:

```ts
type PipeCommandResult = {
  success: boolean
  message: string
  shareUrl?: string | null
}
```

Commands:

| Command | `filePath` | Behavior |
| --- | --- | --- |
| `share` | File path | Creates a file share, copies the public URL to the clipboard, and shows a notification |
| `share-folder-zip` | Folder path | Creates a folder share whose primary entry point is ZIP download |
| `share-folder-browse` | Folder path | Creates a folder share whose primary entry point is folder browsing |
| `receive-here` | Folder path | Creates a receive link for the folder, copies the public URL to the clipboard, and shows a notification |
| `open-dashboard` | Not required | Requests the desktop UI dashboard to open |

Unsupported commands return:

```json
{
  "success": false,
  "message": "Unsupported command.",
  "shareUrl": null
}
```

## Share URL Shapes

The agent builds public URLs from a share's `publicBaseUrl`, token, slug, and entry point.

| Share type | URL shape |
| --- | --- |
| File without slug | `/s/{token}` |
| File with friendly segment | `/s/{token}/{friendlySegment}` |
| Folder browse | `/s/{token}/{slug}` |
| Folder child | `/s/{token}/{slug}/{relativePath}` |
| Folder ZIP | `/s/{token}/{slug}.zip` |
| Current folder ZIP | `/s/{token}/{slug}/{relativePath}?download=zip` |
| Receive link | `/r/{token}` |

Tokens are base62. The recommended token length is 11 characters, with a minimum accepted setting of 6 and a maximum of 128.
