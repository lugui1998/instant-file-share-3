#!/usr/bin/env node
import { createHash, randomBytes } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { performance } from 'node:perf_hooks';
import {
  brotliCompressSync,
  brotliDecompressSync,
  constants as zlibConstants,
  deflateSync,
  gunzipSync,
  gzipSync,
  inflateSync,
} from 'node:zlib';

const MiB = 1024 * 1024;
const KiB = 1024;

const generatedSizes = [
  { label: '64 KiB', bytes: 64 * KiB },
  { label: '1 MiB', bytes: MiB },
  { label: '4 MiB', bytes: 4 * MiB },
];

const desktopSamplePaths = [
  path.join(homedir(), 'Desktop', 'Fabula Ultima [PT-BR] - High-Fantasy.pdf'),
  path.join(homedir(), 'Desktop', 'fixpix-input-1506924553019068426.png'),
];

const algorithms = [
  {
    name: 'gzip',
    levels: [1, 6, 9],
    compress: (input, level) => gzipSync(input, { level }),
    decompress: (input) => gunzipSync(input),
  },
  {
    name: 'deflate',
    levels: [1, 6, 9],
    compress: (input, level) => deflateSync(input, { level }),
    decompress: (input) => inflateSync(input),
  },
  {
    name: 'brotli',
    levels: [1, 5, 11],
    compress: (input, level) =>
      brotliCompressSync(input, {
        params: {
          [zlibConstants.BROTLI_PARAM_QUALITY]: level,
        },
      }),
    decompress: (input) => brotliDecompressSync(input),
  },
];

function parseArgs(argv) {
  const options = {
    outJson: path.join('artifacts', 'compression-benchmark', 'latest.json'),
    outMd: path.join('docs', 'upload-compression-benchmark.md'),
    includeUserSamples: true,
    maxSampleBytes: 32 * MiB,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    const next = argv[index + 1];

    if (arg === '--out-json' && next) {
      options.outJson = next;
      index += 1;
    } else if (arg === '--out-md' && next) {
      options.outMd = next;
      index += 1;
    } else if (arg === '--no-user-samples') {
      options.includeUserSamples = false;
    } else if (arg === '--max-sample-mib' && next) {
      options.maxSampleBytes = Number(next) * MiB;
      index += 1;
    } else if (arg === '--help' || arg === '-h') {
      printHelp();
      process.exit(0);
    } else {
      throw new Error(`Unknown or incomplete argument: ${arg}`);
    }
  }

  if (!Number.isFinite(options.maxSampleBytes) || options.maxSampleBytes <= 0) {
    throw new Error('--max-sample-mib must be a positive number');
  }

  return options;
}

function printHelp() {
  console.log(`Usage: node scripts/compression-benchmark.mjs [options]

Options:
  --out-json <path>       Write detailed JSON results. Default: artifacts/compression-benchmark/latest.json
  --out-md <path>         Write markdown report. Default: docs/upload-compression-benchmark.md
  --no-user-samples       Skip Desktop sample files.
  --max-sample-mib <n>    Cap each external sample read. Default: 32
`);
}

function makeRepeatingBuffer(size, factory) {
  const chunks = [];
  let total = 0;
  let counter = 0;

  while (total < size) {
    const chunk = Buffer.from(factory(counter), 'utf8');
    chunks.push(chunk);
    total += chunk.length;
    counter += 1;
  }

  return Buffer.concat(chunks, total).subarray(0, size);
}

function makeGeneratedSamples() {
  const samples = [];

  for (const size of generatedSizes) {
    samples.push({
      category: 'repetitive-text',
      name: `repetitive text ${size.label}`,
      source: 'generated',
      notes: 'Repeated prose-like text with predictable phrases.',
      data: makeRepeatingBuffer(
        size.bytes,
        (index) =>
          `Instant File Share compression benchmark line ${index % 128}. ` +
          'This content repeats often enough to represent logs, text exports, and markdown. ',
      ),
    });

    samples.push({
      category: 'jsonl',
      name: `JSONL ${size.label}`,
      source: 'generated',
      notes: 'Deterministic structured event records.',
      data: makeRepeatingBuffer(
        size.bytes,
        (index) =>
          JSON.stringify({
            id: index,
            accountId: `acct-${index % 24}`,
            fileName: `upload-${index % 256}.bin`,
            status: index % 7 === 0 ? 'failed' : 'completed',
            bytes: 1024 + (index % 65536),
            tags: ['upload', 'download', `bucket-${index % 12}`],
          }) + '\n',
      ),
    });

    samples.push({
      category: 'csv',
      name: `CSV ${size.label}`,
      source: 'generated',
      notes: 'Deterministic table-like numeric and string data.',
      data: makeRepeatingBuffer(
        size.bytes,
        (index) =>
          `${index},user-${index % 1000},share-${index % 128},${index % 2 === 0},` +
          `${(index * 17) % 100000},2026-06-${String((index % 30) + 1).padStart(2, '0')}\n`,
      ),
    });

    samples.push({
      category: 'random-binary',
      name: `random binary ${size.label}`,
      source: 'generated',
      notes: 'Cryptographically random bytes, expected to be incompressible.',
      data: randomBytes(size.bytes),
    });

    samples.push({
      category: 'zero-filled',
      name: `zero-filled data ${size.label}`,
      source: 'generated',
      notes: 'All zero bytes, an extreme upper bound for compression wins.',
      data: Buffer.alloc(size.bytes),
    });

    samples.push({
      category: 'raw-image-like',
      name: `raw image-like RGB ${size.label}`,
      source: 'generated',
      notes: 'Synthetic uncompressed RGB-ish gradient/noise rows.',
      data: makeRawImageLike(size.bytes),
    });
  }

  return samples;
}

function makeRawImageLike(size) {
  const buffer = Buffer.allocUnsafe(size);
  const width = 512;

  for (let offset = 0; offset < size; offset += 3) {
    const pixel = Math.floor(offset / 3);
    const x = pixel % width;
    const y = Math.floor(pixel / width);
    buffer[offset] = (x + y) & 0xff;
    if (offset + 1 < size) {
      buffer[offset + 1] = (x * 3) & 0xff;
    }
    if (offset + 2 < size) {
      buffer[offset + 2] = (y * 7 + x) & 0xff;
    }
  }

  return buffer;
}

function loadUserSamples(maxSampleBytes) {
  const samples = [];
  const blocked = [];

  for (const samplePath of desktopSamplePaths) {
    try {
      if (!existsSync(samplePath)) {
        blocked.push({ path: samplePath, reason: 'file not found' });
        continue;
      }

      const stats = statSync(samplePath);
      const raw = readFileSync(samplePath);
      const data = raw.length > maxSampleBytes ? raw.subarray(0, maxSampleBytes) : raw;

      samples.push({
        category: path.extname(samplePath).replace('.', '').toLowerCase() || 'user-file',
        name: path.basename(samplePath),
        source: 'desktop-sample',
        path: samplePath,
        originalFileBytes: stats.size,
        capped: raw.length > data.length,
        notes:
          raw.length > data.length
            ? `Read first ${formatBytes(data.length)} of ${formatBytes(raw.length)}.`
            : 'Read full Desktop sample.',
        data,
      });
    } catch (error) {
      blocked.push({ path: samplePath, reason: error.message });
    }
  }

  return { samples, blocked };
}

function benchmarkSample(sample) {
  const inputHash = createHash('sha256').update(sample.data).digest('hex');
  const results = [];

  for (const algorithm of algorithms) {
    for (const level of algorithm.levels) {
      const compressed = timeOperation(() => algorithm.compress(sample.data, level));
      const decompressed = timeOperation(() => algorithm.decompress(compressed.value));
      const outputHash = createHash('sha256').update(decompressed.value).digest('hex');

      if (outputHash !== inputHash) {
        throw new Error(`${algorithm.name} level ${level} failed round-trip for ${sample.name}`);
      }

      const savedBytes = sample.data.length - compressed.value.length;
      const totalCpuSeconds = compressed.seconds + decompressed.seconds;

      results.push({
        algorithm: algorithm.name,
        level,
        inputBytes: sample.data.length,
        compressedBytes: compressed.value.length,
        ratio: compressed.value.length / sample.data.length,
        savedBytes,
        compressionMbps: mbps(sample.data.length, compressed.seconds),
        decompressionMbps: mbps(sample.data.length, decompressed.seconds),
        breakEvenTransferMbps:
          savedBytes > 0 && totalCpuSeconds > 0 ? mbps(savedBytes, totalCpuSeconds) : null,
      });
    }
  }

  return results;
}

function timeOperation(operation) {
  const start = performance.now();
  const value = operation();
  const elapsedMs = performance.now() - start;
  return {
    value,
    seconds: Math.max(elapsedMs / 1000, 0.000001),
  };
}

function mbps(bytes, seconds) {
  return bytes / MiB / seconds;
}

function summarizeSamples(samples, blocked, rows) {
  const bySample = new Map();

  for (const row of rows) {
    const key = row.sampleName;
    const current = bySample.get(key) ?? [];
    current.push(row);
    bySample.set(key, current);
  }

  return samples.map((sample) => {
    const sampleRows = bySample.get(sample.name) ?? [];
    const best = [...sampleRows].sort((a, b) => {
      if (a.ratio !== b.ratio) {
        return a.ratio - b.ratio;
      }
      return (b.breakEvenTransferMbps ?? 0) - (a.breakEvenTransferMbps ?? 0);
    })[0];
    const fastestUseful = [...sampleRows]
      .filter((row) => row.savedBytes > 0)
      .sort((a, b) => b.compressionMbps - a.compressionMbps)[0];

    return {
      name: sample.name,
      category: sample.category,
      source: sample.source,
      bytes: sample.data.length,
      originalFileBytes: sample.originalFileBytes,
      capped: sample.capped ?? false,
      notes: sample.notes,
      recommendation: recommend(sampleRows),
      best,
      fastestUseful,
    };
  });
}

function recommend(rows) {
  const best = [...rows].sort((a, b) => a.ratio - b.ratio)[0];
  const fastUseful = rows.filter((row) => row.savedBytes > 0 && row.compressionMbps >= 50);
  const bestBreakEven = Math.max(...rows.map((row) => row.breakEvenTransferMbps ?? 0));

  if (!best || best.ratio >= 0.98) {
    return 'skip';
  }

  if (best.ratio <= 0.75 && fastUseful.length > 0 && bestBreakEven >= 10) {
    return 'worth compressing';
  }

  return 'maybe';
}

function createMarkdownReport(report) {
  const generatedAt = new Date(report.generatedAt).toISOString();
  const generatedCount = report.samples.filter((sample) => sample.source === 'generated').length;
  const desktopCount = report.samples.filter((sample) => sample.source === 'desktop-sample').length;
  const lines = [
    '# Upload Compression Benchmark',
    '',
    `Generated: ${generatedAt}`,
    '',
    'This benchmark uses Node built-in `zlib` codecs to compare gzip, deflate, and brotli across generated upload-like data and any accessible user sample files. Results are machine-local and intended as decision guidance, not a formal performance guarantee.',
    '',
    '## How to rerun',
    '',
    '```powershell',
    'node scripts/compression-benchmark.mjs --out-json docs/upload-compression-benchmark-results.json --out-md docs/upload-compression-benchmark.md',
    '```',
    '',
    '## Samples',
    '',
    `- Generated samples: ${generatedCount} (${generatedSizes.map((size) => size.label).join(', ')} for each generated category).`,
    `- Desktop samples included: ${desktopCount}.`,
    '- Desktop files are read in place for timing only; the files themselves are not copied into the repository.',
  ];

  if (report.blockedSamples.length > 0) {
    lines.push(
      `- Desktop samples blocked or missing: ${report.blockedSamples.length}.`,
      '',
      '| Path | Reason |',
      '| --- | --- |',
      ...report.blockedSamples.map((sample) => `| \`${sample.path}\` | ${sample.reason} |`),
    );
  }

  lines.push(
    '',
    '## Recommendations',
    '',
    '- Worth compressing: repetitive text, JSONL, CSV, zero-filled data, and raw uncompressed image-like data. gzip/deflate level 1 are usually enough when CPU time matters; brotli level 5 or 11 can save more bytes when latency is less important.',
    '- Maybe: PDFs and other mixed/binary document formats. Test the file shape or compress only when transfer bandwidth is low enough to beat CPU cost.',
    '- Skip: random binary and already-compressed media such as PNG. They can grow after compression and waste CPU.',
    '',
    'Break-even transfer speed means the network throughput below which compression time plus decompression time is paid back by fewer transferred bytes. Higher values mean compression remains useful even on faster links.',
    '',
    '## Summary By Sample',
    '',
    '| Sample | Source | Size | Recommendation | Best codec | Best ratio | Compress MB/s | Decompress MB/s | Break-even MB/s |',
    '| --- | --- | ---: | --- | --- | ---: | ---: | ---: | ---: |',
  );

  for (const sample of report.summary) {
    const best = sample.best;
    lines.push(
      `| ${escapeTable(sample.name)} | ${sample.source}${sample.capped ? ' (capped)' : ''} | ${formatBytes(sample.bytes)} | ${sample.recommendation} | ${best.algorithm}-${best.level} | ${formatNumber(best.ratio)} | ${formatNumber(best.compressionMbps)} | ${formatNumber(best.decompressionMbps)} | ${formatOptional(best.breakEvenTransferMbps)} |`,
    );
  }

  lines.push(
    '',
    '## Fastest Useful Codec By Category',
    '',
    '| Category | Representative recommendation | Fastest useful codec | Ratio | Compress MB/s | Break-even MB/s |',
    '| --- | --- | --- | ---: | ---: | ---: |',
  );

  const categoryRows = new Map();
  for (const sample of report.summary) {
    const current = categoryRows.get(sample.category) ?? [];
    current.push(sample);
    categoryRows.set(sample.category, current);
  }

  for (const [category, samples] of categoryRows) {
    const representative = samples[Math.floor(samples.length / 2)];
    const fastest = representative.fastestUseful ?? representative.best;
    lines.push(
      `| ${category} | ${representative.recommendation} | ${fastest.algorithm}-${fastest.level} | ${formatNumber(fastest.ratio)} | ${formatNumber(fastest.compressionMbps)} | ${formatOptional(fastest.breakEvenTransferMbps)} |`,
    );
  }

  lines.push(
    '',
    '## Notes For Upload/Download Decisions',
    '',
    '- Prefer a small allowlist/denylist over attempting to compress everything. Text-like uploads are strong candidates; already-compressed extensions should usually bypass compression.',
    '- If compression is added to the transfer path, record both original and encoded lengths so the receiver can validate the payload and the UI can show accurate savings.',
    '- Treat brotli quality 11 as an offline or slow-link option. For interactive transfers, gzip or deflate level 1 usually gives most of the win at much lower CPU cost.',
    '',
    'Detailed machine-readable results are written to the JSON output path passed to the script.',
    '',
  );

  return `${lines.join('\n')}\n`;
}

function escapeTable(value) {
  return String(value).replaceAll('|', '\\|');
}

function formatBytes(bytes) {
  if (bytes >= MiB) {
    return `${formatNumber(bytes / MiB)} MiB`;
  }

  if (bytes >= KiB) {
    return `${formatNumber(bytes / KiB)} KiB`;
  }

  return `${bytes} B`;
}

function formatNumber(value) {
  return Number(value).toFixed(2);
}

function formatOptional(value) {
  return value == null ? 'n/a' : formatNumber(value);
}

function ensureParentDirectory(filePath) {
  mkdirSync(path.dirname(filePath), { recursive: true });
}

function main() {
  const options = parseArgs(process.argv.slice(2));
  const generatedSamples = makeGeneratedSamples();
  const { samples: userSamples, blocked } = options.includeUserSamples
    ? loadUserSamples(options.maxSampleBytes)
    : { samples: [], blocked: [] };
  const samples = [...generatedSamples, ...userSamples];
  const rows = [];

  for (const sample of samples) {
    const sampleResults = benchmarkSample(sample);

    for (const result of sampleResults) {
      rows.push({
        sampleName: sample.name,
        category: sample.category,
        source: sample.source,
        ...result,
      });
    }
  }

  const report = {
    generatedAt: new Date().toISOString(),
    node: process.version,
    platform: process.platform,
    maxExternalSampleBytes: options.maxSampleBytes,
    blockedSamples: blocked,
    samples: samples.map((sample) => ({
      name: sample.name,
      category: sample.category,
      source: sample.source,
      bytes: sample.data.length,
      originalFileBytes: sample.originalFileBytes,
      capped: sample.capped ?? false,
      notes: sample.notes,
      path: sample.source === 'desktop-sample' ? sample.path : undefined,
    })),
    summary: summarizeSamples(samples, blocked, rows),
    rows,
  };

  ensureParentDirectory(options.outJson);
  ensureParentDirectory(options.outMd);
  writeFileSync(options.outJson, `${JSON.stringify(report, null, 2)}\n`);
  writeFileSync(options.outMd, createMarkdownReport(report));

  console.log(`Benchmarked ${samples.length} samples and ${rows.length} codec runs.`);
  console.log(`JSON: ${options.outJson}`);
  console.log(`Markdown: ${options.outMd}`);

  if (blocked.length > 0) {
    console.log('Blocked or missing Desktop samples:');
    for (const sample of blocked) {
      console.log(`- ${sample.path}: ${sample.reason}`);
    }
  }
}

main();
