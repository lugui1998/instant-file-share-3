# Local Protocol

## Named pipe

Pipe name: `\\.\pipe\InstantFileShare.Agent`

Example request:

```json
{"command":"share","filePath":"C:\\Users\\me\\Desktop\\archive.zip"}
```

Example response:

```json
{"success":true,"message":"Share link copied to clipboard.","shareUrl":"https://..."}
```

## HTTP API

- `GET /api/runtime`
- `GET /api/shares`
- `POST /api/shares`
- `DELETE /api/shares/{shareId}`
- `GET /api/settings`
- `PUT /api/settings`
- `GET /api/publish-profiles`
- `PUT /api/publish-profiles/{mode}`
- `GET /api/transfers`
- `DELETE /api/transfers/{transferId}`
- `DELETE /api/transfers`
- `GET /api/logs/agent`
- `GET /api/logs/cloudflare`
- `POST /api/cloudflared/detect`
- `POST /api/cloudflared/install`
- `POST /api/cloudflared/update`
- `POST /api/cloudflared/login`
- `POST /api/cloudflared/logout`
- `GET /api/cloudflared/status`
- `GET /api/cloudflared/managed-status`
- `POST /api/cloudflared/managed-check`
- `POST /api/cloudflared/managed-tunnel`
- `GET /ws/runtime`

## Public routes

- `GET /s/{token}`
- `HEAD /s/{token}`
- `GET /s/{token}/{slug}`
- `HEAD /s/{token}/{slug}`
- `GET /r/{token}`
- `HEAD /r/{token}`
- `POST /r/{token}`

Browser-previewable files such as PDFs, common images, and common web video formats are served inline when possible. Other file types are returned as attachments.
