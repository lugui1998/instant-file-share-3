# Browser Download Compression Experiment

This branch prototypes host-compressed, browser-managed downloads for file shares that are likely to benefit from gzip compression.

## Protocol shape

- Raw share URLs remain the canonical fallback.
- Compressible file pages expose `file.rawDownloadUrl` and `file.compressedDownloadUrl`.
- The compressed URL is the raw URL plus `compression=gzip`.
- The compressed response sends gzip bytes as `application/octet-stream` and does not set `Content-Encoding`, because browsers may transparently decode HTTP content-encoded fetch responses before JavaScript sees the stream.
- Experimental response headers:
  - `X-IFS-Transfer-Compression: gzip`
  - `X-IFS-Uncompressed-Length: <original byte length>`

## Candidate policy

Compression is skipped for inline browser-rendered categories and known already-compressed formats:

- images, video, and PDFs
- archive/compression formats such as ZIP, 7z, gzip, brotli, xz, zstd, and rar
- common compressed media and Office package formats such as MP3, MP4, WebM, DOCX, XLSX, and PPTX

Files with unknown or text-like extensions, and extensionless attachments, are treated as candidates.

## Browser behavior

The public-share page uses `DecompressionStream('gzip')` when available. If it is unavailable, the page navigates to the raw download URL.

When decompression is available:

- If `showSaveFilePicker` is available, the decoded stream is piped to the chosen file. This avoids buffering the decoded file in a Blob.
- Otherwise, the decoded stream is converted to a Blob and saved with an object URL.

## Memory concern

The Blob fallback must materialize the full decoded file before the browser save can start. The minimum decoded-byte buffer is therefore approximately the original file size, before browser/runtime overhead. For example, a 512 MB decoded file implies at least about 512 MB of decoded Blob buffering.

The File System Access path is the preferred experiment path because `ReadableStream.pipeTo(writable)` can keep memory bounded by stream buffering instead of the full decoded file size.

## Risks

- `DecompressionStream` and File System Access support are browser-dependent.
- Compressed responses are not range-resumable in this prototype.
- If compression or browser-managed saving fails, the current prototype falls back to a raw download.
