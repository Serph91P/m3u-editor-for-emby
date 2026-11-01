/**
 * Benchmark report generator.
 *
 * Reads results/benchmark-*.json, picks the latest file per mode,
 * and prints three markdown tables to stdout.
 *
 * Usage:
 *   npm run report              # print to terminal
 *   npm run report | pbcopy     # copy for GitHub / Discord
 */

import * as fs from 'fs';
import * as path from 'path';

// ── Types (mirrors helpers.ts without importing Playwright) ───────────────────

interface TimingPair {
  infoTime: number;
  streamTime: number;
}

interface ChannelBenchmark {
  cold: TimingPair;
  warm: TimingPair[];
}

interface BenchmarkFile {
  mode: string;
  version: string;
  commit: string;
  date: string;
  channels: Record<string, ChannelBenchmark>;
}

// ── Constants ─────────────────────────────────────────────────────────────────

const MODES = ['baseline', 'with-stats', 'no-stats'] as const;
type Mode = typeof MODES[number];

const MODE_LABELS: Record<Mode, string> = {
  'baseline':   'Baseline',
  'with-stats': 'Plugin + Stats',
  'no-stats':   'Plugin, No Stats',
};

// ── File loading ──────────────────────────────────────────────────────────────

function loadLatestPerMode(resultsDir: string): Partial<Record<Mode, BenchmarkFile>> {
  const result: Partial<Record<Mode, BenchmarkFile>> = {};

  if (!fs.existsSync(resultsDir)) return result;

  const files = fs.readdirSync(resultsDir)
    .filter(f => f.startsWith('benchmark-') && f.endsWith('.json'))
    .sort(); // lexicographic sort - date suffix means latest is last

  for (const file of files) {
    try {
      const raw = fs.readFileSync(path.join(resultsDir, file), 'utf8');
      const data: BenchmarkFile = JSON.parse(raw);
      if (MODES.includes(data.mode as Mode)) {
        result[data.mode as Mode] = data; // later files overwrite earlier ones
      }
    } catch {
      // Skip malformed files.
    }
  }

  return result;
}

// ── Computation helpers ───────────────────────────────────────────────────────

/** streamTime = -1 means the stream failed to load; exclude from numeric stats. */
function streamFailed(pair: TimingPair): boolean {
  return pair.streamTime < 0;
}

function avg(pairs: TimingPair[], field: keyof TimingPair): number {
  const valid = pairs.filter(p => !streamFailed(p));
  if (!valid.length) return 0;
  return valid.reduce((s, p) => s + p[field], 0) / valid.length;
}

/**
 * Returns the total time for a pair, or undefined if the stream failed.
 * A failed stream (-1) makes the total meaningless, so we return undefined
 * and let callers render it as an error cell.
 */
function total(pair: TimingPair): number | undefined {
  if (streamFailed(pair)) return undefined;
  return pair.infoTime + pair.streamTime;
}

function fmt(n: number): string {
  return n.toFixed(2) + 's';
}

function pct(value: number, baseline: number): string {
  if (baseline === 0) return '';
  const diff = ((value - baseline) / baseline) * 100;
  const sign = diff >= 0 ? '+' : '';
  return ` (${sign}${diff.toFixed(0)}%)`;
}

/**
 * Format a table cell value.
 *   undefined  →  '--'   (data not available; mode was not run)
 *   null       →  'ERR'  (stream failed to load)
 *   number     →  '1.23s' with optional % diff from baseline
 */
function cell(value: number | null | undefined, baseline: number | null | undefined, bold = false): string {
  if (value === undefined) return '--';
  if (value === null) return bold ? '**ERR**' : 'ERR';
  const base = fmt(value);
  const suffix = (baseline !== undefined && baseline !== null) ? pct(value, baseline) : '';
  const text = base + suffix;
  return bold ? `**${text}**` : text;
}

// ── Meta ──────────────────────────────────────────────────────────────────────

function measurementProvenance(data: Partial<Record<Mode, BenchmarkFile>>): string {
  const files = Object.values(data).filter((file): file is BenchmarkFile => !!file);
  const identities = new Set(files.map(file => JSON.stringify([file.version, file.commit, file.date])));
  const lines = ['### Measurement provenance', '', '| Mode | Measured version | Measured commit | Measured date |',
    '|------|------------------|-----------------|---------------|'];
  for (const mode of MODES) {
    const file = data[mode];
    if (file) lines.push(`| ${MODE_LABELS[mode]} | ${file.version ?? 'unknown'} | ${file.commit ?? 'unknown'} | ${file.date ?? 'unknown'} |`);
  }
  if (identities.size > 1) lines.push('', '> **Mixed measurement provenance**: modes were not measured on the same build/run.');
  if (!files.length) lines.push('', '> No measurements available.');
  return lines.join('\n');
}

// ── Table builders ────────────────────────────────────────────────────────────

function buildColdTable(
  channels: string[],
  data: Partial<Record<Mode, BenchmarkFile>>,
): string {
  const baseline = data['baseline'];
  const withStats = data['with-stats'];
  const noStats = data['no-stats'];

  const lines: string[] = [
    '### Cold Start (first play)',
    '',
    '| Channel  | Baseline | Plugin + Stats | Plugin, No Stats |',
    '|----------|----------|----------------|-----------------|',
  ];

  // null = stream failed; undefined = mode not run / channel missing
  const avgs = { baseline: 0, withStats: 0, noStats: 0 };
  let bCount = 0, wCount = 0, nCount = 0;

  for (const ch of channels) {
    const b: number | null | undefined = baseline?.channels[ch]
      ? (total(baseline.channels[ch].cold) ?? null)
      : undefined;
    const w: number | null | undefined = withStats?.channels[ch]
      ? (total(withStats.channels[ch].cold) ?? null)
      : undefined;
    const n: number | null | undefined = noStats?.channels[ch]
      ? (total(noStats.channels[ch].cold) ?? null)
      : undefined;

    lines.push(`| ${ch.padEnd(8)} | ${cell(b, undefined)} | ${cell(w, b)} | ${cell(n, b)} |`);

    if (typeof b === 'number') { avgs.baseline += b; bCount++; }
    if (typeof w === 'number') { avgs.withStats += w; wCount++; }
    if (typeof n === 'number') { avgs.noStats += n; nCount++; }
  }

  const ab = bCount ? avgs.baseline / bCount : (Object.values(baseline?.channels ?? {}).some(ch => ch.cold) ? null : undefined);
  const aw = wCount ? avgs.withStats / wCount : (Object.values(withStats?.channels ?? {}).some(ch => ch.cold) ? null : undefined);
  const an = nCount ? avgs.noStats / nCount : (Object.values(noStats?.channels ?? {}).some(ch => ch.cold) ? null : undefined);

  lines.push(`| **Avg**  | ${cell(ab, undefined, true)} | ${cell(aw, ab, true)} | ${cell(an, ab, true)} |`);

  return lines.join('\n');
}

function buildWarmTable(
  channels: string[],
  data: Partial<Record<Mode, BenchmarkFile>>,
): string {
  const baseline = data['baseline'];
  const withStats = data['with-stats'];
  const noStats = data['no-stats'];

  const lines: string[] = [
    '### Warm Start (avg of 3 runs)',
    '',
    '| Channel  | Baseline | Plugin + Stats | Plugin, No Stats |',
    '|----------|----------|----------------|-----------------|',
  ];

  // Average info + stream across warm runs, excluding failed streams.
  // Returns undefined if there are no warm measurements, null if all streams failed.
  function warmAvgTotal(warm: TimingPair[] | undefined): number | null | undefined {
    if (!warm?.length) return undefined;
    if (!warm.some(p => !streamFailed(p))) return null; // all streams failed
    return avg(warm, 'infoTime') + avg(warm, 'streamTime');
  }

  const avgs = { baseline: 0, withStats: 0, noStats: 0 };
  let bCount = 0, wCount = 0, nCount = 0;

  for (const ch of channels) {
    const bWarm = baseline?.channels[ch]?.warm;
    const wWarm = withStats?.channels[ch]?.warm;
    const nWarm = noStats?.channels[ch]?.warm;

    // null = data exists but all streams failed; undefined = mode not run
    const bTotal: number | null | undefined = bWarm !== undefined
      ? warmAvgTotal(bWarm)
      : undefined;
    const wTotal: number | null | undefined = wWarm !== undefined
      ? warmAvgTotal(wWarm)
      : undefined;
    const nTotal: number | null | undefined = nWarm !== undefined
      ? warmAvgTotal(nWarm)
      : undefined;

    const failures = (pairs: TimingPair[] | undefined) => {
      const count = pairs?.filter(streamFailed).length ?? 0;
      return count ? ` (${count} failed)` : '';
    };
    lines.push(`| ${ch.padEnd(8)} | ${cell(bTotal, undefined)}${failures(bWarm)} | ${cell(wTotal, bTotal)}${failures(wWarm)} | ${cell(nTotal, bTotal)}${failures(nWarm)} |`);

    if (typeof bTotal === 'number') { avgs.baseline += bTotal; bCount++; }
    if (typeof wTotal === 'number') { avgs.withStats += wTotal; wCount++; }
    if (typeof nTotal === 'number') { avgs.noStats += nTotal; nCount++; }
  }

  const ab = bCount ? avgs.baseline / bCount : (Object.values(baseline?.channels ?? {}).some(ch => ch.warm?.length) ? null : undefined);
  const aw = wCount ? avgs.withStats / wCount : (Object.values(withStats?.channels ?? {}).some(ch => ch.warm?.length) ? null : undefined);
  const an = nCount ? avgs.noStats / nCount : (Object.values(noStats?.channels ?? {}).some(ch => ch.warm?.length) ? null : undefined);

  lines.push(`| **Avg**  | ${cell(ab, undefined, true)} | ${cell(aw, ab, true)} | ${cell(an, ab, true)} |`);

  return lines.join('\n');
}

function buildPhaseTable(
  channels: string[],
  data: Partial<Record<Mode, BenchmarkFile>>,
): string {
  const baseline = data['baseline'];
  const withStats = data['with-stats'];
  const noStats = data['no-stats'];

  // Each mode uses its own successful cold cohort for both phases and total.
  function phases(file: BenchmarkFile | undefined) {
    const pairs = channels.map(ch => file?.channels[ch]?.cold).filter((p): p is TimingPair => !!p);
    const valid = pairs.filter(p => !streamFailed(p));
    const mean = (field: keyof TimingPair): number | null | undefined =>
      valid.length ? avg(valid, field) : pairs.length ? null : undefined;
    const info = mean('infoTime');
    const stream = mean('streamTime');
    return { info, stream, total: typeof info === 'number' && typeof stream === 'number' ? info + stream : info };
  }
  const b = phases(baseline), w = phases(withStats), n = phases(noStats);
  const ab_info = b.info, ab_stream = b.stream, ab_total = b.total;
  const aw_info = w.info, aw_stream = w.stream, aw_total = w.total;
  const an_info = n.info, an_stream = n.stream, an_total = n.total;

  const lines: string[] = [
    '### Where Time is Spent (cold start, avg across channels)',
    '',
    '| Phase        | Baseline | Plugin + Stats | Plugin, No Stats |',
    '|--------------|----------|----------------|-----------------|',
    `| Info dialog  | ${cell(ab_info, undefined)} | ${cell(aw_info, ab_info)} | ${cell(an_info, ab_info)} |`,
    `| Stream start | ${cell(ab_stream, undefined)} | ${cell(aw_stream, ab_stream)} | ${cell(an_stream, ab_stream)} |`,
    `| **Total**    | ${cell(ab_total, undefined, true)} | ${cell(aw_total, ab_total, true)} | ${cell(an_total, ab_total, true)} |`,
  ];

  return lines.join('\n');
}

// ── Missing modes note ────────────────────────────────────────────────────────

function missingNote(data: Partial<Record<Mode, BenchmarkFile>>): string | null {
  const missing = MODES.filter(m => !data[m]).map(m => `\`${m}\``);
  if (!missing.length) return null;
  return `> **Note**: Modes still to run: ${missing.join(', ')}. Run \`BENCHMARK_MODE=<mode> npm run benchmark\` to fill them in.`;
}

// ── Main ──────────────────────────────────────────────────────────────────────

function main(): void {
  const resultsDir = path.resolve(__dirname, 'results');
  const data = loadLatestPerMode(resultsDir);

  const date = new Date().toISOString();

  // Collect all channel names seen across any mode (preserve insertion order of BENCHMARK_CHANNELS)
  const BENCHMARK_CHANNELS = ['NPO 1', 'BBC ONE', 'CNN'];
  const seenChannels = new Set<string>(BENCHMARK_CHANNELS);
  for (const file of Object.values(data)) {
    if (file) Object.keys(file.channels).forEach(ch => seenChannels.add(ch));
  }
  const channels = [...seenChannels];

  const output: string[] = [
    '## Emby Channel Switch Benchmark',
    `_Generated ${date}; measurement identity is stored per mode below._`,
    '',
    measurementProvenance(data),
    '',
    buildColdTable(channels, data),
    '',
    buildWarmTable(channels, data),
    '',
    buildPhaseTable(channels, data),
  ];

  const note = missingNote(data);
  if (note) {
    output.push('');
    output.push(note);
  }

  console.log(output.join('\n'));
}

main();
