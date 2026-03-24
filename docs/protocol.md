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
- `POST /api/cloudflared/detect`
- `POST /api/cloudflared/install`
- `POST /api/cloudflared/update`
- `POST /api/cloudflared/login`
- `GET /ws/runtime`

## Public routes

- `GET /s/{token}`
- `HEAD /s/{token}`
- `GET /s/{token}/{slug}`
- `HEAD /s/{token}/{slug}`
