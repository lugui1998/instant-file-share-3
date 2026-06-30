# Receive Upload Compression Experiment

`ReceiveUploadMode.CompressedStream` is an experimental receive-upload mode for one-file-per-request gzip uploads. It is separate from the existing multipart, binary chunk, adaptive chunk, and WebSocket chunk protocols.

## Protocol Shape

- Request: `POST /r/{token}`
- Content-Type: `application/gzip`
- Body: a single gzip stream for one logical file
- Required headers:
  - `X-IFS-Upload-Id`: client upload identifier
  - `X-IFS-Batch-Id`: optional batch identifier for completion notifications
  - `X-IFS-Relative-Path`: URL-encoded logical receive path
  - `X-IFS-File-Name`: URL-encoded display file name
  - `X-IFS-File-Size`: decoded logical byte count

The server streams the decoded bytes into `{destination}.downloadpart`, reserves quota against decoded bytes, validates that the decoded byte count equals `X-IFS-File-Size`, and only renames the partial file after successful decompression and validation.

## Counters

- `ProgressBytes` and `ProgressTotalBytes` track decoded logical bytes.
- Receive-link quota uses decoded logical bytes.
- `BytesSent` records compressed wire bytes where the current transfer model exposes a counter.

The current transfer model does not have separate first-class fields for logical size, compressed wire size, and decoded bytes, so the experiment maps these into existing fields.

## Limitations

- Only gzip is supported.
- Uploads are sequential single-request streams, not resumable chunks.
- Browser support depends on `CompressionStream`.
- There is no compressed WebSocket mode.
- The request must complete before the browser can know the final server validation result.
