import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { discoverSelection, executeDemo, formatCase, reviewPauseMs, storyCommand } from './demo-runner.mjs';

function fixture(t, names = ['AuthService', 'ItemService']) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ase-demo-check-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  for (const name of names) {
    const projectDir = path.join(root, `${name}.UnitTests`);
    fs.mkdirSync(path.join(projectDir, 'Registration'), { recursive: true });
    fs.mkdirSync(path.join(projectDir, 'Login'));
    fs.mkdirSync(path.join(projectDir, 'Support'));
    fs.writeFileSync(path.join(projectDir, `${name}.UnitTests.csproj`), '<Project><TargetFramework>net8.0</TargetFramework></Project>');
    for (const story of ['Registration', 'Login']) fs.writeFileSync(path.join(projectDir, story, 'Tests.cs'),
      `namespace ${name}.UnitTests.${story}; public class Tests { [Fact] public void Works() {} }`);
  }
  return { root, reportDir: path.join(root, 'reports') };
}
function trx(service, story, outcome = 'Passed') {
  return `<TestRun><Results><UnitTestResult testId="id" testName="${service}.UnitTests.${story}.Tests.Works(input: &quot;sample&quot;)"
    outcome="${outcome}" duration="00:00:00.010"><Output><ErrorInfo><Message>Expected true; actual false</Message><StackTrace>at Tests.cs:15</StackTrace></ErrorInfo></Output></UnitTestResult></Results>
    <TestDefinitions><UnitTest id="id"><TestMethod className="${service}.UnitTests.${story}.Tests" name="Works" /></UnitTest></TestDefinitions>
    <ResultSummary outcome="Completed"><Counters total="1" failed="${outcome === 'Failed' ? 1 : 0}" /></ResultSummary></TestRun>`;
}
function fakeRunner({ outcome = 'Passed', missing = false, wrongStory = false, exit = 0, cost = 500, ...options } = {}) {
  let time = 100_000; const lines = []; const calls = []; const waits = [];
  return {
    lines, calls, waits,
    dependencies: {
      now: () => time,
      sleep: async ms => { waits.push(ms); time += ms; },
      write: text => lines.push(text),
      runProcess: async (_command, args) => {
        calls.push(args); time += cost;
        const filter = args[args.indexOf('--filter') + 1];
        const [, service, story] = filter.match(/FullyQualifiedName~(\w+)\.UnitTests\.(\w+)\./);
        const resultDir = args[args.indexOf('--results-directory') + 1];
        if (!missing) fs.writeFileSync(path.join(resultDir, `${service}.${story}.trx`), trx(service, wrongStory ? 'OtherStory' : story, outcome));
        return { code: exit, signal: options.signal ?? null };
      }
    }
  };
}

test('discovery orders services/stories and rejects invalid selections', t => {
  const { root } = fixture(t, ['ItemService', 'AuthService']);
  const selection = discoverSelection(root);
  assert.deepEqual(selection.map(item => item.name), ['AuthService', 'ItemService']);
  assert.deepEqual(selection[0].stories, ['Registration', 'Login']);
  assert.equal(discoverSelection(root, 'AuthService', 'Login')[0].stories.length, 1);
  assert.throws(() => discoverSelection(root, '', 'Login'), /requires a service/);
  assert.throws(() => discoverSelection(root, '../other'), /Invalid/);
  assert.throws(() => discoverSelection(root, 'AuthService', 'Unknown'), /No discoverable/);
});

test('full demo executes each story once in order, preserves terminal output and budgets only service-end pauses', async t => {
  const { root, reportDir } = fixture(t);
  const fake = fakeRunner();
  const result = await executeDemo({ unitRoot: root, reportDir }, fake.dependencies);
  assert.equal(result.exitCode, 0);
  assert.equal(fake.calls.length, 4);
  const text = fake.lines.join('\n');
  const first = text.indexOf('STARTING AUTH SERVICE');
  const registration = text.indexOf('STORY 1/2: REGISTRATION');
  const login = text.indexOf('STORY 2/2: LOGIN');
  const next = text.indexOf('STARTING ITEM SERVICE');
  assert.ok(first < registration && registration < login && login < next);
  assert.equal((text.match(/REVIEW PAUSE:/g) ?? []).length, 2);
  assert.equal(result.timing.presentationDelaySeconds, 107);
  assert.equal(result.timing.executionSeconds, 2);
  assert.equal(result.timing.elapsedSeconds, 109);
  assert.equal((text.match(/TEST 01\/1:/g) ?? []).length, 4);
  assert.match(text, /TOTAL\s+4\s+4\s+0\s+0/);
  assert.doesNotMatch(text, /^[ \t]+\S/m);
  assert.doesNotMatch(text, /\x1b/);
  assert.deepEqual(result.timing.stories.map(item => `${item.service}/${item.story}`),
    ['AuthService/Registration', 'AuthService/Login', 'ItemService/Registration', 'ItemService/Login']);
  assert.ok(fs.existsSync(path.join(reportDir, 'demo-timing.json')));
});

test('single service and single story retain selection and have no review delays', async t => {
  for (const story of ['', 'Login']) {
    const { root, reportDir } = fixture(t);
    const fake = fakeRunner();
    const result = await executeDemo({ unitRoot: root, reportDir, service: 'AuthService', story }, fake.dependencies);
    assert.equal(result.exitCode, 0);
    assert.equal(fake.calls.length, story ? 1 : 2);
    assert.equal(fake.waits.length, 0);
    assert.equal(result.timing.targetSeconds, null);
  }
});

test('slow execution consumes the budget without extra delays or cutting tests short', async t => {
  const { root, reportDir } = fixture(t);
  const fake = fakeRunner({ cost: 60_000 });
  const result = await executeDemo({ unitRoot: root, reportDir }, fake.dependencies);
  assert.equal(result.exitCode, 0);
  assert.equal(fake.calls.length, 4);
  assert.equal(fake.waits.length, 0);
  assert.equal(result.timing.elapsedSeconds, 240);
  assert.equal(reviewPauseMs(1000, 112_000, 4, 4), 0);
});

test('failed, skipped-only, missing and incorrectly scoped results never pass', async t => {
  for (const options of [{ outcome: 'Failed', exit: 1 }, { outcome: 'NotExecuted' }, { missing: true }, { wrongStory: true }, { exit: 1 }]) {
    const { root, reportDir } = fixture(t);
    const fake = fakeRunner(options);
    const result = await executeDemo({ unitRoot: root, reportDir, service: 'AuthService', story: 'Login' }, fake.dependencies);
    assert.equal(result.exitCode, 1);
    if (options.outcome === 'Failed') {
      assert.match(fake.lines.join('\n'), /RESULT: FAIL/);
      assert.match(fake.lines.join('\n'), /Expected true; actual false/);
      assert.match(fake.lines.join('\n'), /Tests.cs:15/);
    }
  }
});

test('prepared runs invoke the cached assembly with the exact story filter and refuse missing caches', t => {
  const { root } = fixture(t, ['AuthService']);
  const project = path.join(root, 'AuthService.UnitTests', 'AuthService.UnitTests.csproj');
  const artifacts = path.join(root, 'build');
  const assemblyDir = path.join(artifacts, 'bin', 'AuthService.UnitTests', 'release');
  fs.mkdirSync(assemblyDir, { recursive: true });
  const assembly = path.join(assemblyDir, 'AuthService.UnitTests.dll'); fs.writeFileSync(assembly, 'test fixture');
  const args = ['--no-build', '--no-restore', '--artifacts-path', artifacts, '--filter', 'Name~Works'];
  const command = storyCommand(project, 'AuthService', 'Login', path.join(root, 'results'), args);
  assert.equal(command[0], 'vstest');
  assert.equal(command[1], assembly);
  assert.ok(command.includes('--TestCaseFilter:(FullyQualifiedName~AuthService.UnitTests.Login.)&(Name~Works)'));
  assert.ok(command.includes('--logger:trx;LogFileName=AuthService.Login.trx'));
  assert.throws(() => storyCommand(project, 'AuthService', 'Login', root,
    ['--no-build', '--no-restore', '--artifacts-path', path.join(root, 'absent')]), /Prepare first/);
  const fallback = storyCommand(project, 'AuthService', 'Login', root, [...args, '--verbosity', 'quiet']);
  assert.equal(fallback[0], 'test');
  assert.equal(fallback.filter(arg => arg === '--filter').length, 1);
});

test('case formatting is unindented and does not claim verification for failed/skipped cases', () => {
  const base = { name: 'Example(input: "wrong")', methodName: 'RejectsInvalidInput', description: 'Rejects invalid input.', durationMs: 3 };
  assert.match(formatCase({ ...base, status: 'passed' }, 0, 2), /VERIFIED: Rejects invalid input/);
  const failed = formatCase({ ...base, status: 'failed', error: 'Expected true\n  Actual false', stackTrace: '  at File.cs:9' }, 1, 2);
  assert.match(failed, /CHECKS: Rejects invalid input/);
  assert.doesNotMatch(failed, /VERIFIED:/);
  assert.doesNotMatch(failed, /^[ \t]+\S/m);
  assert.match(failed, /TEST 02\/2/);
});

test('existing story-result directories cannot be silently reused', async t => {
  const { root, reportDir } = fixture(t);
  fs.mkdirSync(path.join(reportDir, 'results', 'AuthService', 'Registration'), { recursive: true });
  const fake = fakeRunner();
  await assert.rejects(executeDemo({ unitRoot: root, reportDir, service: 'AuthService' }, fake.dependencies), /Refusing to reuse/);
  assert.equal(fake.calls.length, 0);
});
