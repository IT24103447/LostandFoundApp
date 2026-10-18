// Unit-specific reporting. Reads the standard VSTest TRX files produced by run.sh.
// No browser, Python, or report-generator package is needed.
// Usage: node summarize-results.mjs <trx-directory> <output-directory>
import fs from 'node:fs';
import path from 'node:path';
import { readTrx, trxFiles, readable, storyScopes, serviceOrder, storyOrder } from './unit-report-data.mjs';

const [inputDir, outputDir] = process.argv.slice(2);
if (!inputDir || !outputDir) {
  console.error('Usage: node summarize-results.mjs <trx-directory> <output-directory>');
  process.exit(2);
}
fs.mkdirSync(outputDir, { recursive: true });

let cases = [];
try {
  const files = trxFiles(inputDir);
  if (files.length === 0) throw new Error('No TRX files were produced. Inspect console.log for build/test failures.');
  for (const file of files) cases.push(...readTrx(file));
  if (!cases.some(test => test.status !== 'skipped')) throw new Error('No tests executed; every recorded case was skipped.');
} catch (error) {
  cases.push({ name: 'Unit-test execution evidence', description: 'Requires complete result files containing actual executed tests.', status: 'failed', error: error.message });
}
// Reporting groups are derived from executed TRX cases, never from assumed story coverage.
const html = value => String(value ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;')
  .replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');
// Escape table delimiters and Markdown control characters as well as raw HTML.
const cell = value => html(value).replaceAll('|', '&#124;').replace(/([\\`*_\[\]])/g, '\\$1').replace(/\r?\n/g, '<br>');
const code = value => `<code>${html(value).replaceAll('|', '&#124;').replace(/\r?\n/g, '<br>')}</code>`;
function totals(tests) {
  return {
    total: tests.length,
    run: tests.filter(test => test.status !== 'skipped').length,
    ...Object.fromEntries(['passed', 'failed', 'skipped'].map(status => [status, tests.filter(test => test.status === status).length])),
    durationMs: tests.reduce((sum, test) => sum + (test.durationMs ?? 0), 0)
  };
}
const counts = totals(cases);
const failed = cases.filter(test => test.status === 'failed');
const runFailed = counts.failed > 0 || Number(process.env.ASE_UNIT_EXIT_CODE ?? 0) !== 0;
function groups(tests, key, order = []) {
  const grouped = new Map();
  for (const test of tests) {
    const name = key(test);
    if (!grouped.has(name)) grouped.set(name, []);
    grouped.get(name).push(test);
  }
  const rank = name => order.includes(name) ? order.indexOf(name) : order.length;
  return [...grouped].sort(([a], [b]) => rank(a) - rank(b) || a.localeCompare(b))
    .map(([name, tests]) => ({ name, tests, ...totals(tests) }));
}
const serviceGroups = groups(cases, test => test.service ?? 'Execution checks', serviceOrder)
  .map(service => ({ ...service, stories: groups(service.tests, test => test.story ?? 'Execution evidence', storyOrder) }));
function methods(story) {
  return groups(story.tests, test => `${test.testClass ?? ''}.${test.methodName ?? test.name}`)
    .map((group, index) => ({ ...group, id: `C${String(index + 1).padStart(2, '0')}` }));
}
function label(test) {
  const name = readable(test.methodName ?? test.name);
  return name.length <= 64 ? name : `${name.slice(0, 61).replace(/\s+\S*$/, '')}…`;
}
function parameters(test) {
  const start = test.name.indexOf('(');
  return start < 0 ? 'No parameters' : test.name.slice(start + 1, test.name.endsWith(')') ? -1 : undefined);
}
const outcome = group => group.failed ? 'FAIL' : group.run ? 'PASS' : 'SKIP';
const countCells = group => `${group.run} | ${group.passed} | ${group.failed} | ${group.skipped}`;
const startMs = Number(process.env.ASE_UNIT_STARTED_MS);
const elapsed = Number.isFinite(startMs) && startMs > 0 && startMs <= Date.now()
  ? Math.round((Date.now() - startMs) / 10) / 100 : null;
const demoMode = process.env.ASE_UNIT_DEMO === 'true';
const timingFile = path.join(outputDir, 'demo-timing.json');
const demoTiming = demoMode && fs.existsSync(timingFile) ? JSON.parse(fs.readFileSync(timingFile, 'utf8')) : null;
const summary = [
  '# Unit testing', '',
  `**Result: ${runFailed ? 'FAILED' : 'PASSED'}** · **${counts.total} recorded cases** · ${counts.passed} passed · ${counts.failed} failed · ${counts.skipped} skipped`, '',
  ...(elapsed !== null ? [`**Suite runtime:** ${elapsed.toFixed(2)} s (selected projects: restore/build, tests and report preparation).`, ''] : []),
  ...(demoTiming ? [`**Demo timing:** ${demoTiming.executionSeconds.toFixed(2)} s execution/result reading; ${demoTiming.presentationDelaySeconds.toFixed(2)} s presentation delays. Delays are not test execution time.`, ''] : []),
  ...(process.env.ASE_UNIT_SCOPE ? [`**Selected scope:** ${cell(process.env.ASE_UNIT_SCOPE)}`, ''] : []),
  ...(runFailed ? ['## Failures and execution problems', '',
    ...(Number(process.env.ASE_UNIT_EXIT_CODE ?? 0) !== 0 ? ['The test command returned a nonzero exit code. Inspect console.log for build or execution errors; available TRX results may cover only part of the selected suite.', ''] : []),
    ...failed.flatMap(test => [
      `<details open><summary>${html(readable(test.service ?? 'Execution check'))} / ${html(readable(test.story ?? 'Evidence'))}: ${html(label(test))}</summary>`, '',
      `**Exact case:** ${code(test.name)}`, '',
      `<pre>${html(test.error)}${test.stackTrace ? `\n\n${html(test.stackTrace)}` : ''}</pre>`, '', '</details>', ''
    ])] : []),
  '| Service | Stories tested | Tests run | Passed | Failed | Skipped |',
  '|---|---:|---:|---:|---:|---:|',
  ...serviceGroups.map(service => `| ${cell(readable(service.name))} | ${service.stories.length} | ${countCells(service)} |`),
  `| **Total** | **${serviceGroups.reduce((sum, service) => sum + service.stories.length, 0)}** | **${counts.run}** | **${counts.passed}** | **${counts.failed}** | **${counts.skipped}** |`, '',
  'Each parameterized case is counted individually. Tests run = passed + failed; skipped cases are reported separately. Only stories present in the result files are listed.', '',
  'Expand a service, then a story, to see the checks and exact case parameters. A passing unit check demonstrates the named code path for its supplied inputs; it does not establish that every story acceptance criterion is implemented.', '',
  ...serviceGroups.flatMap(service => [
    `<details${service.failed ? ' open' : ''}><summary><strong>${html(readable(service.name))}</strong> — ${service.run} tests run; ${service.passed} passed, ${service.failed} failed, ${service.skipped} skipped</summary>`, '',
    '| Story | Tests run | Passed | Failed | Skipped |', '|---|---:|---:|---:|---:|',
    ...service.stories.map(story => `| ${cell(readable(story.name))} | ${countCells(story)} |`), '',
    ...service.stories.flatMap(story => {
      const checks = methods(story);
      return [
        `<details${story.failed ? ' open' : ''}><summary><strong>${html(readable(story.name))}</strong> — ${story.run} tests run; ${checks.length} checks; ${outcome(story)}</summary>`, '',
        `**Tested scope:** ${cell(storyScopes[story.name] ?? 'The executed unit checks listed below.')}`, '',
        '| Check | Cases | What it verifies | Passed | Failed | Skipped |', '|---|---:|---|---:|---:|---:|',
        ...checks.map(check => `| ${check.id}: ${cell(label(check.tests[0]))} | ${check.total} | ${cell(check.tests[0].description)} | ${check.passed} | ${check.failed} | ${check.skipped} |`), '',
        '**Evidence limits:** Repository/API/AI/email stubs verify interactions and returned behaviour, not live persistence, delivery or service integration. DTO/policy/scoring checks cover the code exercised. Frontend logic run in Node does not establish browser behaviour. No code-coverage percentage or complete acceptance-criteria coverage is inferred.', '',
        '<details><summary>Individual case details — exact identifiers, parameters and timings</summary>', '',
        ...checks.flatMap(check => [
          `**${check.id}: ${cell(readable(check.tests[0].methodName ?? check.tests[0].name))}**`, '',
          `**Identifier:** ${code(check.name.replace(/^\./, ''))}`, '',
          '| Case | Parameters | Result | Duration ms |', '|---|---|---|---:|',
          ...check.tests.map((test, index) => `| ${index + 1} | ${code(parameters(test))} | ${test.status.toUpperCase()} | ${test.durationMs ?? '—'} |`), '',
          ...check.tests.filter(test => test.skipReason).map(test => `Skip reason (${code(parameters(test))}): ${cell(test.skipReason)}`), ''
        ]), '</details>', '', '</details>', ''
      ];
    }), '</details>', ''
  ]),
  '<details><summary>Timing diagnostics — five slowest cases</summary>', '',
  `**Summed case duration:** ${counts.durationMs} ms. Cases can run in parallel; this sum is not suite wall-clock time.`, '',
  '| Service / story | Check | Parameters | Duration ms |', '|---|---|---|---:|',
  ...[...cases].filter(test => test.durationMs !== undefined).sort((a, b) => b.durationMs - a.durationMs).slice(0, 5)
    .map(test => `| ${cell(`${readable(test.service)} / ${readable(test.story)}`)} | ${cell(label(test))} | ${code(parameters(test))} | ${test.durationMs} |`), '',
  '</details>', '',
  'Full evidence is retained in the ase-unit-testing artifact: summary.md, details.json, console.log and native results/*.trx. These unit checks do not establish deployment, real email/Kafka delivery, database integration or end-to-end UI behaviour.', ''
].join('\n');
fs.writeFileSync(path.join(outputDir, 'summary.md'), summary);
fs.writeFileSync(path.join(outputDir, 'details.json'), JSON.stringify({
  cases,
  summary: { ...counts, result: runFailed ? 'failed' : 'passed', wallClockSeconds: elapsed,
    ...(demoTiming ? { executionSeconds: demoTiming.executionSeconds, presentationDelaySeconds: demoTiming.presentationDelaySeconds } : {}) },
  services: serviceGroups.map(service => ({
    service: service.name, ...totals(service.tests),
    stories: service.stories.map(story => ({ story: story.name, ...totals(story.tests) }))
  }))
}, null, 2) + '\n');
// One service line and one story line replace hundreds of passing-case log lines.
for (const service of demoMode ? [] : serviceGroups) {
  console.log(`\n${readable(service.name)}: ${service.passed} passed | ${service.failed} failed | ${service.skipped} skipped`);
  for (const story of service.stories) {
    console.log(`  ${outcome(story)}  ${readable(story.name)}: ${story.run} tests run (${story.passed} passed, ${story.failed} failed, ${story.skipped} skipped)`);
  }
}
for (const test of failed.filter(test => !demoMode || !test.testClass)) console.log(`\nFAIL ${test.name}\n${test.error}${test.stackTrace ? `\n${test.stackTrace}` : ''}`);
if (runFailed && !counts.failed) console.error('Test execution failed; inspect console.log even though available TRX cases passed.');
if (demoMode) console.log(`\nFINAL RESULT: ${runFailed ? 'FAILED' : 'PASSED'}`);
console.log(`\n${counts.passed} passed | ${counts.failed} failed | ${counts.skipped} skipped. Expand summary.md or download the artifact for individual cases.`);
process.exitCode = runFailed ? 1 : 0;
