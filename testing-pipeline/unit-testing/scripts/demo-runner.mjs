// Executes fresh tests one story at a time. No stored report is replayed.
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readTrx, readable, serviceOrder, storyOrder, storyScopes } from './unit-report-data.mjs';

const rule = '='.repeat(64);
const divider = '-'.repeat(64);
const quantity = (count, noun, plural = `${noun}s`) => `${count} ${count === 1 ? noun : plural}`;
const safeName = /^[A-Za-z][A-Za-z0-9_]*$/;
const orderBy = order => (a, b) => {
  const rank = name => order.includes(name) ? order.indexOf(name) : order.length;
  return rank(a) - rank(b) || a.localeCompare(b);
};
const sourceFiles = dir => fs.readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
  const file = path.join(dir, entry.name);
  return entry.isDirectory() ? sourceFiles(file) : entry.name.endsWith('.cs') ? [file] : [];
});

export function discoverSelection(unitRoot, service = '', story = '') {
  if ((service && !safeName.test(service)) || (story && !safeName.test(story))) throw new Error('Invalid service/story name.');
  if (story && !service) throw new Error('A story requires a service.');
  const names = service ? [service] : fs.readdirSync(unitRoot, { withFileTypes: true })
    .filter(entry => entry.isDirectory() && entry.name.endsWith('.UnitTests'))
    .map(entry => entry.name.slice(0, -'.UnitTests'.length)).sort(orderBy(serviceOrder));
  return names.map(name => {
    const directory = path.join(unitRoot, `${name}.UnitTests`);
    const project = path.join(directory, `${name}.UnitTests.csproj`);
    if (!fs.existsSync(project)) throw new Error(`No test project exists for ${name}.`);
    const stories = fs.readdirSync(directory, { withFileTypes: true })
      .filter(entry => entry.isDirectory() && !['Support', 'bin', 'obj', 'TestResults'].includes(entry.name))
      .filter(entry => safeName.test(entry.name) && sourceFiles(path.join(directory, entry.name)).some(file =>
        /\[(?:Fact|Theory)(?:\s*\(|\s*\])/.test(fs.readFileSync(file, 'utf8'))))
      .map(entry => entry.name).filter(name => !story || name === story).sort(orderBy(storyOrder));
    if (!stories.length) throw new Error(`No discoverable story folders for ${name}${story ? ` / ${story}` : ''}.`);
    return { name, project, stories };
  });
}

// Four checkpoints spread review pauses after services, including the last one.
// Leave one second for final report preparation. Never add delay after a deadline.
export function reviewPauseMs(started, now, completed, total, target = 110_000) {
  return Math.max(0, Math.floor((target - Math.min(1000, target)) * completed / total - (now - started)));
}

export function storyCommand(project, service, story, resultsDir, dotnetArgs = []) {
  const args = []; let artifacts; let direct = true;
  const filters = [];
  for (let index = 0; index < dotnetArgs.length; index++) {
    const arg = dotnetArgs[index];
    if (arg === '--filter') {
      if (!dotnetArgs[index + 1]) throw new Error('--filter requires an expression.');
      filters.push(dotnetArgs[++index]);
    } else if (arg.startsWith('--filter=')) filters.push(arg.slice('--filter='.length));
    else {
      args.push(arg);
      if (arg === '--artifacts-path') {
        artifacts = dotnetArgs[++index];
        if (!artifacts) throw new Error('--artifacts-path requires a directory.');
        args.push(artifacts);
      } else if (!['--no-build', '--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false'].includes(arg)) direct = false;
    }
  }
  const filter = [`FullyQualifiedName~${service}.UnitTests.${story}.`, ...filters].map(value => `(${value})`).join('&');
  const fileName = `${service}.${story}.trx`;
  if (direct && args.includes('--no-build') && args.includes('--no-restore')) {
    const xml = fs.readFileSync(project, 'utf8');
    const assemblyName = xml.match(/<AssemblyName>([^<]+)<\/AssemblyName>/)?.[1] ?? path.basename(project, '.csproj');
    const framework = xml.match(/<TargetFramework>([^<]+)<\/TargetFramework>/)?.[1];
    const assembly = artifacts
      ? path.join(artifacts, 'bin', path.basename(project, '.csproj'), 'release', `${assemblyName}.dll`)
      : path.join(path.dirname(project), 'bin', 'Release', framework ?? 'net8.0', `${assemblyName}.dll`);
    if (!fs.existsSync(assembly)) throw new Error(`Prepared assembly not found: ${assembly}. Run .\\test-unit.ps1 -Prepare first.`);
    return ['vstest', assembly, `--TestCaseFilter:${filter}`, `--logger:trx;LogFileName=${fileName}`,
      '--logger:console;verbosity=detailed', `--ResultsDirectory:${resultsDir}`];
  }
  return ['test', project, '-c', 'Release', ...args, '--filter', filter,
    '--logger', 'console;verbosity=detailed', '--logger', `trx;LogFileName=${fileName}`, '--results-directory', resultsDir];
}

export function formatCase(test, index, total) {
  const status = { passed: 'PASS', failed: 'FAIL', skipped: 'SKIP' }[test.status];
  const input = test.name.includes('(') ? test.name.slice(test.name.indexOf('(') + 1, test.name.endsWith(')') ? -1 : undefined)
    : 'Fixed fixture in the linked test source; no parameters.';
  return [
    `TEST ${String(index + 1).padStart(2, '0')}/${total}: ${readable(test.methodName ?? test.name)}`,
    `RESULT: ${status} | ${test.durationMs ?? 0} ms`,
    `INPUT: ${input.replace(/\r?\n/g, '\\n')}`,
    `${test.status === 'passed' ? 'VERIFIED' : 'CHECKS'}: ${test.description.replace(/^Verifies:\s*/, '')}`,
    ...(test.error ? [`ERROR: ${test.error}`, ...(test.stackTrace ? [`LOCATION / STACK: ${test.stackTrace}`] : [])] : []),
    ...(test.skipReason ? [`SKIP REASON: ${test.skipReason}`] : []), divider, ''
  ].map(line => line.split(/\r?\n/).map(part => part.trimStart()).join('\n')).join('\n');
}

function runProcess(command, args, logFile) {
  return new Promise((resolve, reject) => {
    const fd = fs.openSync(logFile, 'a');
    const child = spawn(command, args, { stdio: ['ignore', fd, fd], windowsHide: true });
    child.once('error', reject);
    child.once('close', (code, signal) => { fs.closeSync(fd); resolve({ code: code ?? 1, signal }); });
  });
}

export async function executeDemo(options, injected = {}) {
  const { unitRoot, reportDir, service = '', story = '', dotnetArgs = [] } = options;
  const now = injected.now ?? Date.now;
  const sleep = injected.sleep ?? (ms => new Promise(resolve => setTimeout(resolve, ms)));
  const run = injected.runProcess ?? runProcess;
  const write = injected.write ?? (text => console.log(text));
  const selection = discoverSelection(unitRoot, service, story);
  if (!selection.length) throw new Error('No test services found.');
  const fullSuite = !service && !story && selection.length > 1;
  const started = options.startedMs ?? now();
  let executionMs = 0; let delayMs = 0; let failedExecution = false;
  const results = []; const stages = [];
  fs.mkdirSync(reportDir, { recursive: true });
  const emit = (...lines) => write(lines.join('\n'));
  emit(rule, 'UNIT TEST DEMO', `${quantity(selection.length, 'service')} | ${quantity(selection.reduce((sum, entry) => sum + entry.stories.length, 0), 'story', 'stories')}`,
    fullSuite ? 'Full-suite target: 110 seconds, with adaptive pauses after services.' : 'Selected scope: no automatic service-end review pauses.',
    'Each story executes before its results are printed. Full diagnostics stay in console.log.', rule, '');
  for (const [serviceIndex, item] of selection.entries()) {
    emit(rule, `[${serviceIndex + 1}/${selection.length}] STARTING ${readable(item.name).toUpperCase()}`, quantity(item.stories.length, 'story', 'stories'), rule, '');
    const serviceTests = []; let serviceErrors = 0;
    for (const [storyIndex, storyName] of item.stories.entries()) {
      emit(rule, `STORY ${storyIndex + 1}/${item.stories.length}: ${readable(storyName).toUpperCase()}`,
        `TESTS: ${storyScopes[storyName] ?? 'The unit checks in this story folder.'}`, 'Running tests...', rule, '');
      const resultsDir = path.join(reportDir, 'results', item.name, storyName);
      if (fs.existsSync(resultsDir)) throw new Error(`Refusing to reuse existing story results: ${resultsDir}`);
      fs.mkdirSync(resultsDir, { recursive: true });
      const stageStarted = now(); let code = 1; let tests = []; let error = '';
      try {
        const command = storyCommand(item.project, item.name, storyName, resultsDir, dotnetArgs);
        const child = await run('dotnet', command, path.join(reportDir, 'console.log'));
        code = child.code;
        if (child.signal) throw new Error(`Test execution interrupted: ${child.signal}`);
        tests = readTrx(path.join(resultsDir, `${item.name}.${storyName}.trx`));
        if (tests.some(test => test.service !== item.name || test.story !== storyName)) throw new Error('TRX includes cases outside the selected story.');
      } catch (exception) { error = exception.message; tests = []; }
      const stageMs = now() - stageStarted; executionMs += stageMs;
      const failed = tests.filter(test => test.status === 'failed').length;
      const skipped = tests.filter(test => test.status === 'skipped').length;
      const passed = tests.filter(test => test.status === 'passed').length;
      if (error || code !== 0 || !tests.length || tests.length === skipped) {
        failedExecution = true; serviceErrors++;
        emit(`EXECUTION PROBLEM: ${error || `Test command exit ${code}; executed ${tests.length - skipped} cases.`}`, 'Inspect console.log; missing/unexecuted cases are not passes.', '');
      }
      if (failed) failedExecution = true;
      for (const [index, test] of [...tests].sort((a, b) => a.name.localeCompare(b.name)).entries()) write(formatCase(test, index, tests.length));
      emit(`STORY COMPLETE: ${readable(storyName).toUpperCase()}`, `${passed} passed | ${failed} failed | ${skipped} skipped`, '');
      serviceTests.push(...tests); stages.push({ service: item.name, story: storyName, code, passed, failed, skipped, executionMs: stageMs, error });
    }
    const total = status => serviceTests.filter(test => test.status === status).length;
    results.push({ service: item.name, stories: item.stories.length, passed: total('passed'), failed: total('failed'), skipped: total('skipped'), executionErrors: serviceErrors });
    emit(rule, `${readable(item.name).toUpperCase()} COMPLETE`, `${total('passed')} passed | ${total('failed')} failed | ${total('skipped')} skipped`,
      `Service result: ${serviceErrors || total('failed') ? 'FAILED' : 'PASSED'}`, rule, '');
    const pause = fullSuite ? reviewPauseMs(started, now(), serviceIndex + 1, selection.length) : 0;
    if (pause > 0) {
      emit(`REVIEW PAUSE: ${(pause / 1000).toFixed(1)} seconds`,
        serviceIndex + 1 < selection.length ? `Next service: ${readable(selection[serviceIndex + 1].name)}` : 'Next: combined final report.', '');
      const pauseStarted = now();
      // Short waits keep Ctrl+C responsive and never clear the terminal.
      for (let remaining = pause; remaining > 0;) { await sleep(Math.min(remaining, 1000)); remaining = pause - (now() - pauseStarted); }
      delayMs += now() - pauseStarted;
    }
  }
  const timing = { mode: 'demo', targetSeconds: fullSuite ? 110 : null, executionSeconds: executionMs / 1000,
    presentationDelaySeconds: delayMs / 1000, elapsedSeconds: (now() - started) / 1000, services: results, stories: stages };
  fs.writeFileSync(path.join(reportDir, 'demo-timing.json'), JSON.stringify(timing, null, 2) + '\n');
  const grand = field => results.reduce((sum, entry) => sum + entry[field], 0);
  emit(rule, `DEMO EXECUTION: ${failedExecution ? 'FAILED' : 'PASSED'}`, rule,
    'Service                  Stories  Passed  Failed  Skipped',
    ...results.map(entry => `${readable(entry.service).padEnd(25)}${String(entry.stories).padEnd(9)}${String(entry.passed).padEnd(8)}${String(entry.failed).padEnd(8)}${entry.skipped}`), '',
    `${'TOTAL'.padEnd(25)}${String(grand('stories')).padEnd(9)}${String(grand('passed')).padEnd(8)}${String(grand('failed')).padEnd(8)}${grand('skipped')}`, '');
  return { exitCode: failedExecution ? 1 : 0, timing };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [unitRoot, reportDir, service = '', story = '', ...dotnetArgs] = process.argv.slice(2);
  try {
    if (!unitRoot || !reportDir) throw new Error('Usage: demo-runner.mjs <unit-root> <report-dir> <service-or-empty> <story-or-empty> [dotnet args...]');
    const candidate = Number(process.env.ASE_UNIT_STARTED_MS);
    const result = await executeDemo({ unitRoot, reportDir, service, story, dotnetArgs,
      ...(Number.isFinite(candidate) && candidate > 0 && candidate <= Date.now() ? { startedMs: candidate } : {}) });
    process.exitCode = result.exitCode;
  } catch (error) { console.error(`Demo execution failed: ${error.message}`); process.exitCode = 1; }
}
