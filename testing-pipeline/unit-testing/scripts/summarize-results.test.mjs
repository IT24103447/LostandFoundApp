// Reporting checks only: node --test testing-pipeline/unit-testing/scripts/summarize-results.test.mjs
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import test from 'node:test';

const reporter = fileURLToPath(new URL('./summarize-results.mjs', import.meta.url));
const xml = value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;')
  .replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&apos;');

function trx(cases) {
  return `<TestRun><Results>${cases.map((entry, index) => `<UnitTestResult testId="${index}"
    testName="${xml(`${entry.className}.${entry.method}${entry.parameters ? `(${entry.parameters})` : ''}`)}"
    outcome="${entry.outcome ?? 'Passed'}" duration="00:00:00.012">
    ${entry.message ? `<Output><ErrorInfo><Message>${xml(entry.message)}</Message><StackTrace>${xml(entry.stack ?? '')}</StackTrace></ErrorInfo></Output>` : ''}
    </UnitTestResult>`).join('')}</Results><TestDefinitions>${cases.map((entry, index) =>
    `<UnitTest id="${index}"><TestMethod className="${entry.className}" name="${entry.method}" /></UnitTest>`).join('')}
    </TestDefinitions><ResultSummary outcome="Completed"><Counters total="${cases.length}"
    failed="${cases.filter(entry => entry.outcome === 'Failed').length}" /></ResultSummary></TestRun>`;
}

function report(t, files, env = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ase-unit-report-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const input = path.join(root, 'input');
  const output = path.join(root, 'output');
  fs.mkdirSync(input);
  for (const [name, content] of Object.entries(files)) fs.writeFileSync(path.join(input, name), content);
  const inherited = { ...process.env };
  for (const name of ['ASE_UNIT_STARTED_MS', 'ASE_UNIT_EXIT_CODE', 'ASE_UNIT_SCOPE', 'ASE_UNIT_DEMO']) delete inherited[name];
  const processResult = spawnSync(process.execPath, [reporter, input, output], {
    encoding: 'utf8', env: { ...inherited, ...env }
  });
  assert.ifError(processResult.error);
  assert.equal(processResult.signal, null);
  return {
    exitCode: processResult.status,
    log: processResult.stdout + processResult.stderr,
    markdown: fs.readFileSync(path.join(output, 'summary.md'), 'utf8'),
    details: JSON.parse(fs.readFileSync(path.join(output, 'details.json'), 'utf8'))
  };
}

test('groups parameter cases by service, story and class/method without losing case counts', t => {
  const result = report(t, { 'suite.trx': trx([
    { className: 'AuthService.UnitTests.Login.CredentialTests', method: 'RejectsInvalidCredentials', parameters: 'email: "unknown"' },
    { className: 'AuthService.UnitTests.Login.CredentialTests', method: 'RejectsInvalidCredentials', parameters: 'password: "wrong"' },
    { className: 'AuthService.UnitTests.Registration.RequiredTests', method: 'RejectsEmptyName' },
    { className: 'ItemService.UnitTests.ReportLostItem.CreateTests', method: 'CreatesActiveItem' }
  ]) });
  assert.equal(result.exitCode, 0);
  assert.match(result.markdown, /\| Auth Service \| 2 \| 3 \| 3 \| 0 \| 0 \|/);
  assert.match(result.markdown, /\| Login \| 2 \| 2 \| 0 \| 0 \|/);
  assert.match(result.markdown, /C01: Rejects Invalid Credentials \| 2 \|/);
  assert.equal(result.details.cases.length, 4);
  assert.equal(result.details.services[0].stories.find(story => story.story === 'Login').total, 2);
  assert.match(result.markdown, /<summary>Individual case details/);
  assert.match(result.markdown, /email: &quot;unknown&quot;/);
  assert.match(result.markdown, /AuthService\.UnitTests\.Login\.CredentialTests\.RejectsInvalidCredentials/);
  assert.doesNotMatch(result.log, /PASS\s+RejectsInvalidCredentials/);
  assert.match(result.log, /PASS  Login: 2 tests run/);
});

test('same method name in different classes remains two checks', t => {
  const result = report(t, { 'suite.trx': trx([
    { className: 'AuthService.UnitTests.Login.First', method: 'RequiresIdentity' },
    { className: 'AuthService.UnitTests.Login.Second', method: 'RequiresIdentity' }
  ]) });
  assert.match(result.markdown, /Login<\/strong> — 2 tests run; 2 checks/);
});

test('failed cases retain exact parameters, full error and stack trace, with failure sections open', t => {
  const result = report(t, { 'suite.trx': trx([
    { className: 'ItemService.UnitTests.ReportLostItem.CreateTests', method: 'RejectsInvalidInput',
      parameters: 'text: "<script> | **details** `code` & more"', outcome: 'Failed',
      message: 'Expected <safe> | actual\nsecond line', stack: 'at CreateTests.cs:123\nfull stack' }
  ]) });
  assert.equal(result.exitCode, 1);
  assert.ok(result.markdown.indexOf('## Failures') < result.markdown.indexOf('| Service |'));
  assert.match(result.markdown, /<details open><summary><strong>Item Service/);
  assert.match(result.markdown, /&lt;script&gt; &#124;/);
  assert.doesNotMatch(result.markdown, /<script>/);
  assert.match(result.markdown, /at CreateTests\.cs:123\nfull stack/);
  assert.equal(result.details.cases[0].error, 'Expected <safe> | actual\nsecond line');
  assert.equal(result.details.cases[0].stackTrace, 'at CreateTests.cs:123\nfull stack');
  assert.match(result.log, /FAIL ItemService\.UnitTests/);
});

test('skipped cases are separate from executed cases and keep their reason', t => {
  const result = report(t, { 'suite.trx': trx([
    { className: 'AuthService.UnitTests.Login.Tests', method: 'Works' },
    { className: 'AuthService.UnitTests.Login.Tests', method: 'Deferred', outcome: 'NotExecuted', message: 'Not supported here' }
  ]) });
  assert.equal(result.exitCode, 0);
  assert.match(result.markdown, /\| Auth Service \| 1 \| 1 \| 1 \| 0 \| 1 \|/);
  assert.equal(result.details.cases[1].skipReason, 'Not supported here');
  assert.match(result.markdown, /Not supported here/);
});

test('missing, truncated, inconsistent and all-skipped evidence cannot produce a passing report', t => {
  const skipped = { className: 'AuthService.UnitTests.Login.Tests', method: 'Deferred', outcome: 'NotExecuted' };
  for (const files of [{}, { 'suite.trx': '<TestRun>' },
    { 'suite.trx': trx([skipped]).replace('total="1"', 'total="2"') }, { 'suite.trx': trx([skipped]) }]) {
    const result = report(t, files);
    assert.equal(result.exitCode, 1);
    assert.match(result.markdown, /\*\*Result: FAILED\*\*/);
  }
});

test('a failed build/test command cannot be hidden by a partial passing TRX', t => {
  const result = report(t, { 'suite.trx': trx([
    { className: 'AuthService.UnitTests.Login.Tests', method: 'Works' }
  ]) }, { ASE_UNIT_EXIT_CODE: '1' });
  assert.equal(result.exitCode, 1);
  assert.match(result.markdown, /available TRX results may cover only part/);
  assert.equal(result.details.summary.passed, 1);
  assert.equal(result.details.summary.result, 'failed');
});

test('runtime is measured independently of summed case durations and selected scope is escaped', t => {
  const result = report(t, { 'suite.trx': trx([
    { className: 'AuthService.UnitTests.Login.Tests', method: 'Works' }
  ]) }, { ASE_UNIT_STARTED_MS: String(Date.now() - 1500), ASE_UNIT_SCOPE: 'AuthService <Login>' });
  assert.equal(result.exitCode, 0);
  assert.ok(result.details.summary.wallClockSeconds >= 1.5);
  assert.match(result.markdown, /\*\*Suite runtime:\*\*/);
  assert.match(result.markdown, /Summed case duration:\*\* 12 ms/);
  assert.match(result.markdown, /AuthService &lt;Login&gt;/);
});
