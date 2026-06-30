# Upload Compression Benchmark

Generated: 2026-06-30T15:12:59.741Z

This benchmark uses Node built-in `zlib` codecs to compare gzip, deflate, and brotli across generated upload-like data and any accessible user sample files. Results are machine-local and intended as decision guidance, not a formal performance guarantee.

## How to rerun

```powershell
node scripts/compression-benchmark.mjs --out-json docs/upload-compression-benchmark-results.json --out-md docs/upload-compression-benchmark.md
```

## Samples

- Generated samples: 18 (64 KiB, 1 MiB, 4 MiB for each generated category).
- Desktop samples included: 2.
- Desktop files are read in place for timing only; the files themselves are not copied into the repository.

## Recommendations

- Worth compressing: repetitive text, JSONL, CSV, zero-filled data, and raw uncompressed image-like data. gzip/deflate level 1 are usually enough when CPU time matters; brotli level 5 or 11 can save more bytes when latency is less important.
- Maybe: PDFs and other mixed/binary document formats. Test the file shape or compress only when transfer bandwidth is low enough to beat CPU cost.
- Skip: random binary and already-compressed media such as PNG. They can grow after compression and waste CPU.

Break-even transfer speed means the network throughput below which compression time plus decompression time is paid back by fewer transferred bytes. Higher values mean compression remains useful even on faster links.

## Summary By Sample

| Sample | Source | Size | Recommendation | Best codec | Best ratio | Compress MB/s | Decompress MB/s | Break-even MB/s |
| --- | --- | ---: | --- | --- | ---: | ---: | ---: | ---: |
| repetitive text 64 KiB | generated | 64.00 KiB | worth compressing | brotli-11 | 0.00 | 3.33 | 29.83 | 2.98 |
| JSONL 64 KiB | generated | 64.00 KiB | worth compressing | brotli-11 | 0.04 | 1.37 | 290.56 | 1.30 |
| CSV 64 KiB | generated | 64.00 KiB | worth compressing | brotli-11 | 0.10 | 1.45 | 251.31 | 1.30 |
| random binary 64 KiB | generated | 64.00 KiB | skip | brotli-1 | 1.00 | 437.68 | 1790.83 | n/a |
| zero-filled data 64 KiB | generated | 64.00 KiB | worth compressing | brotli-5 | 0.00 | 466.07 | 420.03 | 220.88 |
| raw image-like RGB 64 KiB | generated | 64.00 KiB | worth compressing | brotli-11 | 0.41 | 0.70 | 167.25 | 0.41 |
| repetitive text 1 MiB | generated | 1.00 MiB | worth compressing | brotli-11 | 0.00 | 37.77 | 1069.86 | 36.47 |
| JSONL 1 MiB | generated | 1.00 MiB | worth compressing | brotli-11 | 0.04 | 1.06 | 995.82 | 1.03 |
| CSV 1 MiB | generated | 1.00 MiB | worth compressing | brotli-11 | 0.08 | 0.98 | 522.41 | 0.91 |
| random binary 1 MiB | generated | 1.00 MiB | skip | brotli-1 | 1.00 | 1030.18 | 1820.17 | n/a |
| zero-filled data 1 MiB | generated | 1.00 MiB | worth compressing | brotli-5 | 0.00 | 1325.03 | 469.51 | 346.66 |
| raw image-like RGB 1 MiB | generated | 1.00 MiB | worth compressing | brotli-11 | 0.16 | 1.22 | 545.20 | 1.02 |
| repetitive text 4 MiB | generated | 4.00 MiB | worth compressing | brotli-11 | 0.00 | 97.93 | 931.62 | 88.61 |
| JSONL 4 MiB | generated | 4.00 MiB | worth compressing | brotli-11 | 0.03 | 0.94 | 1060.61 | 0.91 |
| CSV 4 MiB | generated | 4.00 MiB | worth compressing | brotli-11 | 0.06 | 0.97 | 628.91 | 0.91 |
| random binary 4 MiB | generated | 4.00 MiB | skip | brotli-1 | 1.00 | 990.47 | 1666.25 | n/a |
| zero-filled data 4 MiB | generated | 4.00 MiB | worth compressing | brotli-5 | 0.00 | 1556.30 | 460.85 | 355.56 |
| raw image-like RGB 4 MiB | generated | 4.00 MiB | worth compressing | brotli-11 | 0.04 | 3.76 | 1073.83 | 3.60 |
| Fabula Ultima [PT-BR] - High-Fantasy.pdf | desktop-sample | 16.48 MiB | maybe | brotli-11 | 0.78 | 0.66 | 227.04 | 0.14 |
| fixpix-input-1506924553019068426.png | desktop-sample | 1.02 MiB | skip | brotli-11 | 0.99 | 0.62 | 198.00 | 0.01 |

## Fastest Useful Codec By Category

| Category | Representative recommendation | Fastest useful codec | Ratio | Compress MB/s | Break-even MB/s |
| --- | --- | --- | ---: | ---: | ---: |
| repetitive-text | worth compressing | brotli-1 | 0.00 | 3287.31 | 800.45 |
| jsonl | worth compressing | gzip-1 | 0.10 | 882.69 | 402.02 |
| csv | worth compressing | brotli-1 | 0.21 | 374.27 | 161.45 |
| random-binary | skip | brotli-1 | 1.00 | 1030.18 | n/a |
| zero-filled | worth compressing | brotli-1 | 0.00 | 2639.22 | 742.15 |
| raw-image-like | worth compressing | brotli-1 | 0.50 | 405.22 | 118.03 |
| pdf | maybe | brotli-1 | 0.82 | 513.70 | 48.10 |
| png | skip | brotli-1 | 0.99 | 880.38 | 5.14 |

## Notes For Upload/Download Decisions

- Prefer a small allowlist/denylist over attempting to compress everything. Text-like uploads are strong candidates; already-compressed extensions should usually bypass compression.
- If compression is added to the transfer path, record both original and encoded lengths so the receiver can validate the payload and the UI can show accurate savings.
- Treat brotli quality 11 as an offline or slow-link option. For interactive transfers, gzip or deflate level 1 usually gives most of the win at much lower CPU cost.

Detailed machine-readable results are written to the JSON output path passed to the script.

