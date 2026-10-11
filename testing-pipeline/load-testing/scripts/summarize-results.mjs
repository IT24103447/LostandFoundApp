// Turns a JMeter results file into the pipeline's summary.md and details.json and checks the thresholds.
// Usage: node summarize-results.mjs <results.jtl> <thresholds.json> <output-dir>
// Exit code: 0 when every threshold passes, 1 when any fails or no results exist.
import fs from 'node:fs';
import path from 'node:path';

const [jtlPath, thresholdsPath, outDir] = process.argv.slice(2);
if (!jtlPath || !thresholdsPath || !outDir) {
  console.error('Usage: node summarize-results.mjs <results.jtl> <thresholds.json> <output-dir>');
  process.exit(2);
}

const GROUPS = {
  steady: { match: /steady/i, phase: 'ramp-up and steady peak (users join one by one, then hold, with think time)' },
  spike: { match: /spike/i, phase: 'spike (users start together, no think time)' },
  admin: { match: /admin/i, phase: 'admin working throughout the run' },
};

const ENDPOINTS = {
  'Auth - login': 'Logs in through Auth Service with real credentials; expects 200 and a JWT in the response.',
  'Auth - me': 'Reads the signed-in user\'s profile with the JWT; expects 200 and an email.',
  'Auth - admin user list': 'Admin reads the paged user list; expects 200 and a list of users.',
  'Item - search': 'Searches listings with varied terms, categories and types; expects 200 and a paged result.',
  'Item - view listing': 'Opens a listing found by the search; expects 200 and the same listing id.',
  'Item - my lost reports': 'Reads the user\'s own lost reports; expects 200 and a list.',
  'Item - my found reports': 'Reads the user\'s own found reports; expects 200 and a list.',
  'Item - create lost listing': 'Creates a lost listing (no photo, unique title); expects 201 and the new id. Publishes a Kafka event.',
  'Matching - my matches': 'Reads the user\'s matches (Matching also calls Item Service); expects 200.',
  'Matching - my claims': 'Reads the user\'s claims; expects 200.',
  'Matching - my appeals': 'Reads the user\'s appeals; expects 200.',
  'AdminVerify - spam review list': 'Admin reads the active Spam Review queue, which grows during the run; expects 200.',
};

function parseCsv(text) {
  const rows = [];
  let row = [], field = '', quoted = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quoted) {
      if (ch === '"' && text[i + 1] === '"') { field += '"'; i++; }
      else if (ch === '"') quoted = false;
      else field += ch;
    } else if (ch === '"') quoted = true;
    else if (ch === ',') { row.push(field); field = ''; }
    else if (ch === '\n') { row.push(field); rows.push(row); row = []; field = ''; }
    else if (ch !== '\r') field += ch;
  }
  if (field !== '' || row.length) { row.push(field); rows.push(row); }
  return rows;
}

function percentile(sorted, q) {
  return sorted.length ? sorted[Math.min(sorted.length - 1, Math.ceil(q * sorted.length) - 1)] : 0;
}

function stats(samples) {
  const times = samples.map(s => s.elapsed).sort((a, b) => a - b);
  const errors = samples.filter(s => !s.success).length;
  const start = Math.min(...samples.map(s => s.timeStamp));
  const end = Math.max(...samples.map(s => s.timeStamp + s.elapsed));
  const seconds = Math.max((end - start) / 1000, 0.001);
  return {
    users: Math.max(...samples.map(s => s.grpThreads)),
    requests: samples.length,
    errors,
    errorRatePercent: +(errors * 100 / samples.length).toFixed(2),
    p50Ms: percentile(times, 0.5),
    p90Ms: percentile(times, 0.9),
    p95Ms: percentile(times, 0.95),
    p99Ms: percentile(times, 0.99),
    maxMs: times[times.length - 1],
    throughputPerSec: +(samples.length / seconds).toFixed(2),
  };
}

function write(summary, details) {
  fs.mkdirSync(outDir, { recursive: true });
  fs.writeFileSync(path.join(outDir, 'summary.md'), summary);
  fs.writeFileSync(path.join(outDir, 'details.json'), JSON.stringify(details, null, 2) + '\n');
}

const thresholds = JSON.parse(fs.readFileSync(thresholdsPath, 'utf8'));
const maxErrorRate = thresholds.errorRatePercent.max;
const rows = fs.existsSync(jtlPath) ? parseCsv(fs.readFileSync(jtlPath, 'utf8').trim()) : [];

if (rows.length < 2) {
  write('# load-testing\n\nNo JMeter results were recorded, so no load tests can be reported. Inspect console.log and jmeter.log.\n',
    { cases: [{ name: 'Load test results recorded', description: 'JMeter must record at least one request result.', status: 'failed', error: `No results in ${jtlPath}` }] });
  console.error('No JMeter results found.');
  process.exit(1);
}

const header = rows.shift();
const col = name => header.indexOf(name);
const samples = rows.filter(r => r.length === header.length).map(r => {
  const thread = r[col('threadName')].replace(/ \d+-\d+$/, '');
  const group = Object.keys(GROUPS).find(g => GROUPS[g].match.test(thread)) ?? thread;
  return {
    timeStamp: +r[col('timeStamp')],
    elapsed: +r[col('elapsed')],
    label: r[col('label')],
    code: r[col('responseCode')],
    success: r[col('success')] === 'true',
    failure: r[col('failureMessage')] || r[col('responseMessage')],
    thread,
    group,
    grpThreads: +r[col('grpThreads')],
    path: (r[col('URL')] || '').replace(/^https?:\/\/[^/]+/, '').replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi, '{id}').replace(/\?.*$/, ''),
  };
});

const byKey = new Map();
for (const s of samples) {
  const key = `${s.thread}\u0000${s.label}`;
  if (!byKey.has(key)) byKey.set(key, []);
  byKey.get(key).push(s);
}

const cases = [];
const overall = stats(samples);
const overallPass = overall.errorRatePercent <= maxErrorRate;
cases.push({
  name: 'Overall error rate',
  description: `Every request across all thread groups and endpoints must succeed (status and body assertions). Passes when no more than ${maxErrorRate}% of all requests fail.`,
  status: overallPass ? 'passed' : 'failed',
  ...overall,
  threshold: { errorRateMaxPercent: maxErrorRate },
  ...(overallPass ? {} : { error: `${overall.errorRatePercent}% of requests failed (limit ${maxErrorRate}%).` }),
});

const threadGroups = [...new Set(samples.map(s => s.thread))];
for (const thread of threadGroups) {
  const groupSamples = samples.filter(s => s.thread === thread);
  const g = stats(groupSamples);
  const group = groupSamples[0].group;
  const pass = g.errorRatePercent <= maxErrorRate;
  cases.push({
    name: `${thread} (thread group)`,
    description: `All requests made during the ${GROUPS[group]?.phase ?? thread} phase. Passes when no more than ${maxErrorRate}% fail.`,
    status: pass ? 'passed' : 'failed',
    ...g,
    threshold: { errorRateMaxPercent: maxErrorRate },
    ...(pass ? {} : { error: `${g.errorRatePercent}% of requests failed (limit ${maxErrorRate}%).` }),
  });
}

const endpointRows = [];
for (const [key, list] of byKey) {
  const [thread, label] = key.split('\u0000');
  const group = list[0].group;
  const limits = thresholds.p95Ms[group] ?? {};
  const p95Limit = limits[label] ?? limits.default;
  const e = stats(list);
  const problems = [];
  if (e.errorRatePercent > maxErrorRate) problems.push(`${e.errorRatePercent}% failed (limit ${maxErrorRate}%)`);
  if (p95Limit !== undefined && e.p95Ms > p95Limit) problems.push(`p95 ${e.p95Ms} ms (limit ${p95Limit} ms)`);
  const failures = [...new Set(list.filter(s => !s.success).map(s => `${s.code} ${s.failure}`.trim()))].slice(0, 3);
  cases.push({
    name: `${thread} | ${label}`,
    description: `${ENDPOINTS[label] ?? `Requests named "${label}".`} Passes when p95 ≤ ${p95Limit ?? 'n/a'} ms and no more than ${maxErrorRate}% fail.`,
    status: problems.length ? 'failed' : 'passed',
    endpoint: list[0].path,
    ...e,
    threshold: { p95MaxMs: p95Limit ?? null, errorRateMaxPercent: maxErrorRate },
    ...(problems.length ? { error: [...problems, ...failures.map(f => `e.g. ${f}`)].join('; ') } : {}),
  });
  endpointRows.push({ thread, label, path: list[0].path, p95Limit, problems, ...e });
}

const failed = cases.filter(c => c.status === 'failed');
const durationSec = Math.round((Math.max(...samples.map(s => s.timeStamp + s.elapsed)) - Math.min(...samples.map(s => s.timeStamp))) / 1000);
const groupUsers = threadGroups.map(t => `${t}: ${Math.max(...samples.filter(s => s.thread === t).map(s => s.grpThreads))} users`).join(', ');

const md = [
  '# load-testing',
  '',
  `**Result: ${failed.length ? `FAILED (${failed.length} of ${cases.length} checks)` : `PASSED (${cases.length} checks)`}**`,
  '',
  `Scope: Apache JMeter load test of the Lost & Found services. Thread groups run: ${groupUsers}. Duration ${durationSec} s. Endpoints exercised: ${new Set(samples.map(s => s.label)).size}.`,
  '',
  `Totals: ${overall.requests} requests, ${overall.errors} failed (${overall.errorRatePercent}%), ${overall.throughputPerSec} requests/s, p95 ${overall.p95Ms} ms.`,
  '',
  '## Thresholds',
  '',
  `- Overall error rate ≤ ${maxErrorRate}%: ${overallPass ? 'passed' : 'FAILED'} (${overall.errorRatePercent}%)`,
  `- p95 per endpoint within its thread group's limit: ${endpointRows.every(r => !r.problems.some(p => p.startsWith('p95'))) ? 'passed' : 'FAILED'}`,
  '',
  '## Endpoints',
  '',
  '| Thread group | Endpoint | Path | Requests | Errors | p50 ms | p90 ms | p95 ms | p99 ms | Req/s | p95 limit | Result |',
  '|---|---|---|---|---|---|---|---|---|---|---|---|',
  ...endpointRows.map(r => `| ${r.thread} | ${r.label} | \`${r.path}\` | ${r.requests} | ${r.errors} | ${r.p50Ms} | ${r.p90Ms} | ${r.p95Ms} | ${r.p99Ms} | ${r.throughputPerSec} | ${r.p95Limit ?? '–'} | ${r.problems.length ? 'FAILED' : 'passed'} |`),
  '',
  ...(failed.length ? ['## Failures', '', ...failed.map(c => `- **${c.name}:** ${c.error}`), ''] : []),
  'Native reports: `results.jtl` (raw JMeter results) and `html/index.html` (JMeter dashboard).',
  '',
].join('\n');

write(md, { cases });
console.log(md);
process.exit(failed.length ? 1 : 0);
