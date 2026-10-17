// Converts a VSTest .trx file into the pipeline's details.json + summary.md.
// Descriptions come from [Trait("Description", "...")] in the test sources, because TRX does not carry them.
// Usage: node trx-to-report.mjs <file.trx> <testsSourceDir> <reportDir>
import fs from 'node:fs';
import path from 'node:path';

const [trxPath, testsDir, reportDir] = process.argv.slice(2);
if (!trxPath || !testsDir || !reportDir) {
  console.error('Usage: trx-to-report.mjs <file.trx> <testsSourceDir> <reportDir>');
  process.exit(2);
}
fs.mkdirSync(reportDir, { recursive: true });

// 1) Descriptions from source: the Description trait sits above "public void NAME".
const descriptions = new Map();
for (const file of fs.readdirSync(testsDir).filter(f => f.endsWith('.cs'))) {
  const src = fs.readFileSync(path.join(testsDir, file), 'utf8');
  const re = /\[Trait\("Description",\s*"((?:[^"\\]|\\.)*)"\)\][\s\S]*?public\s+(?:async\s+)?(?:void|Task)\s+(\w+)\s*\(/g;
  let m;
  while ((m = re.exec(src)) !== null) descriptions.set(m[2], m[1].replace(/\\"/g, '"'));
}

// 2) Results from TRX.
if (!fs.existsSync(trxPath)) {
  const msg = 'No TRX file was produced; the test project probably failed to build or start.';
  fs.writeFileSync(path.join(reportDir, 'summary.md'), `# ui-e2e-testing\n\n${msg}\n`);
  fs.writeFileSync(path.join(reportDir, 'details.json'), JSON.stringify({ cases: [] }, null, 2));
  console.error(msg);
  process.exit(1);
}
const xml = fs.readFileSync(trxPath, 'utf8');
const decode = s => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
const attr = (tag, name) => { const m = new RegExp(`\\b${name}="([^"]*)"`).exec(tag); return m ? decode(m[1]) : undefined; };
const toMs = d => {
  const m = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(d || '');
  return m ? Math.round((((+(m[1] || 0)) * 24 + +m[2]) * 3600 + +m[3] * 60 + parseFloat(m[4])) * 1000) : undefined;
};

const cases = [];
const resultRe = /<UnitTestResult\b([^>]*?)(\/>|>([\s\S]*?)<\/UnitTestResult>)/g;
let r;
while ((r = resultRe.exec(xml)) !== null) {
  const tag = r[1];
  const fullName = attr(tag, 'testName') ?? 'unknown';
  const name = fullName.split('.').pop().replace(/\(.*$/, '');
  const outcome = (attr(tag, 'outcome') ?? '').toLowerCase();
  const status = outcome === 'passed' ? 'passed' : outcome === 'failed' ? 'failed'
    : (outcome === 'notexecuted' || outcome === 'skipped') ? 'skipped' : 'failed';
  const body = r[3] ?? '';
  const msg = /<Message>([\s\S]*?)<\/Message>/.exec(body);
  const c = {
    name,
    description: descriptions.get(name) ?? `UI/E2E test ${name}`,
    status,
    durationMs: toMs(attr(tag, 'duration')),
  };
  if (status === 'failed') c.error = decode(msg ? msg[1] : 'Test failed; see console.log').trim().split('\n').slice(0, 6).join(' | ');
  cases.push(c);
}
cases.sort((a, b) => a.name.localeCompare(b.name));

fs.writeFileSync(path.join(reportDir, 'details.json'), JSON.stringify({ cases }, null, 2) + '\n');


// Scope is derived from test-name prefixes, so the report never claims more than what ran.
const AREAS = {
  AUTH: 'authentication and route guards', BRW: 'browse, search and filter',
  LOST: 'report-lost wizard', FOUND: 'report-found wizard', RPT: 'my reports (view, edit, delete)',
  ADM: 'admin pages', FLOW: 'cross-service journeys', NEG: 'robustness / negative cases',
};
function scopeAreas(list) {
  const seen = new Set(list.map(c => c.name.split('_')[0]));
  return Object.keys(AREAS).filter(k => seen.has(k)).map(k => AREAS[k]).join(', ');
}

const count = s => cases.filter(c => c.status === s).length;
const total = cases.reduce((n, c) => n + (c.durationMs ?? 0), 0);
const lines = [
  '# UI / E2E testing (Selenium + xUnit, Chrome headless)',
  '',
  `Passed: ${count('passed')}  |  Failed: ${count('failed')}  |  Skipped: ${count('skipped')}  |  Total: ${cases.length}`,
  `Summed test time: ${(total / 1000).toFixed(1)} s`,
  '',
  `Scope (from the tests that ran): ${scopeAreas(cases) || 'none'}.`,
  'Out of scope: real email (OTP), Gemini image analysis, cross-browser, visual regression.',
  '',
  '| Test | Status | Time (s) | What it checks |',
  '|---|---|---|---|',
  ...cases.map(c => `| ${c.name} | ${c.status} | ${c.durationMs !== undefined ? (c.durationMs / 1000).toFixed(1) : '-'} | ${c.description} |`),
];
const failed = cases.filter(c => c.status === 'failed');
if (failed.length) {
  lines.push('', '## Failures', '', ...failed.map(c => `- **${c.name}**: ${c.error}`));
  lines.push('', 'Screenshots of failed tests are in the `screenshots` folder next to this report.');
}
fs.writeFileSync(path.join(reportDir, 'summary.md'), lines.join('\n') + '\n');
console.log(`Report written: ${cases.length} cases, ${failed.length} failed.`);
