// Exercises the real TypeScript module. API imports are replaced with stubs;
// fake timers record delays and complete immediately. No network or npm install.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { stripTypeScriptTypes } from 'node:module';

const scenario = process.argv[2];
const calls = [];
const waits = [];
const listing = { listingId: 'listing-1', listingType: 'lost', solveResult: null };
const detail = { kickStatus: 'NOT_REQUESTED', listings: [listing] };
const input = { recordId: 'record-1', userId: 'user-1', userName: 'Test User', labels: { 'listing-1': 'Blue bag' }, kick: false, resume: false };
let deleteFailures = 0;
let deleteStatus = 500;
let kickError;
let startError;
let saveError = false;
let finishError = false;

switch (scenario) {
  case 'five-attempt-limit': deleteFailures = 5; break;
  case 'retry-succeeds': deleteFailures = 2; break;
  case 'skip-404': deleteFailures = 1; deleteStatus = 404; break;
  case 'skip-409': deleteFailures = 1; deleteStatus = 409; break;
  case 'resume':
    input.resume = true;
    detail.listings = [{ ...listing, listingId: 'deleted', solveResult: 'DELETED' }, { ...listing, listingId: 'skipped', solveResult: 'SKIPPED' }, { ...listing, solveResult: 'FAILED' }];
    break;
  case 'kick-checked': input.kick = true; detail.kickStatus = 'PENDING'; break;
  case 'kick-unchecked': break;
  case 'kick-refused': detail.kickStatus = 'PENDING'; kickError = { status: 403 }; break;
  case 'already-kicked': detail.kickStatus = 'PENDING'; kickError = { status: 400, body: { error: 'User is already kicked.' } }; break;
  case 'kick-not-repeated': input.resume = true; detail.kickStatus = 'FAILED'; break;
  case 'save-result-failure': saveError = true; break;
  case 'start-forbidden': startError = { status: 403, body: { error: 'Forbidden' } }; break;
  case 'finish-failure': finishError = true; break;
  case 'failed-delete-still-kicks': deleteFailures = 5; detail.kickStatus = 'PENDING'; break;
  case 'success': break;
  default: throw new Error(`Unknown scenario ${scenario}`);
}

const stub = (name, action = async () => {}) => async (...args) => {
  calls.push({ name, args });
  return action(...args);
};
const api = {
  startSolve: stub('start', async () => { if (startError) throw startError; return detail; }),
  deleteAdminItem: stub('delete', async () => { if (deleteFailures-- > 0) throw { status: deleteStatus }; }),
  saveListingResult: stub('save-listing', async () => { if (saveError) throw new Error('database unavailable'); }),
  saveKickResult: stub('save-kick'),
  finishSolve: stub('finish', async () => { if (finishError) throw { body: { error: 'Finish unavailable' } }; }),
};
const kickUser = stub('kick', async () => { if (kickError) throw kickError; });
const context = vm.createContext({ window: { setTimeout: (callback, ms) => { waits.push(ms); callback(); return waits.length; } } });
const source = fs.readFileSync('frontend/src/features/admin/solve/runSolve.ts', 'utf8');
const module = new vm.SourceTextModule(stripTypeScriptTypes(source), { context });
await module.link(async specifier => {
  const values = specifier.endsWith('/admin') ? { kickUser } : api;
  return new vm.SyntheticModule(Object.keys(values), function () {
    for (const [key, value] of Object.entries(values)) this.setExport(key, value);
  }, { context });
});
await module.evaluate();
let state = { phase: 'running', listings: {}, errors: [], kick: 'not-requested' };
await module.namespace.runSolve(input, change => { state = change(state); });
const count = name => calls.filter(call => call.name === name).length;
const saved = calls.find(call => call.name === 'save-listing')?.args[2];
const kicked = calls.find(call => call.name === 'save-kick')?.args[1];

switch (scenario) {
  case 'five-attempt-limit':
    assert.equal(count('delete'), 5); assert.equal(waits.length, 4);
    assert.ok(waits.every(ms => ms === 1000)); assert.equal(saved, 'FAILED');
    assert.equal(state.phase, 'stopped'); assert.equal(count('finish'), 0); break;
  case 'retry-succeeds':
    assert.equal(count('delete'), 3); assert.equal(waits.length, 2); assert.equal(saved, 'DELETED'); break;
  case 'skip-404': case 'skip-409':
    assert.equal(count('delete'), 1); assert.equal(waits.length, 0); assert.equal(saved, 'SKIPPED'); break;
  case 'resume':
    assert.equal(count('delete'), 1); assert.equal(calls.find(c => c.name === 'delete').args[1], 'listing-1');
    assert.equal(calls[0].args[2], true); break;
  case 'kick-checked':
    assert.equal(count('kick'), 1); assert.equal(kicked, 'KICKED');
    assert.ok(calls.findIndex(c => c.name === 'kick') > calls.findIndex(c => c.name === 'save-listing')); break;
  case 'kick-unchecked': assert.equal(count('kick'), 0); assert.equal(count('save-kick'), 0); break;
  case 'kick-refused':
    assert.equal(kicked, 'FAILED'); assert.ok(state.errors.includes("Couldn't kick Test User. Kick them from User Management.")); break;
  case 'already-kicked': assert.equal(kicked, 'KICKED'); assert.equal(state.errors.length, 0); break;
  case 'kick-not-repeated': assert.equal(count('kick'), 0); assert.equal(count('save-kick'), 0); break;
  case 'save-result-failure': assert.equal(state.phase, 'stopped'); assert.equal(count('finish'), 0); break;
  case 'start-forbidden':
    assert.equal(state.phase, 'stopped'); assert.equal(count('delete'), 0); assert.ok(state.errors.includes('Forbidden')); break;
  case 'finish-failure': assert.equal(state.phase, 'stopped'); assert.ok(state.errors.includes('Finish unavailable')); break;
  case 'failed-delete-still-kicks':
    assert.equal(count('kick'), 1); assert.equal(count('delete'), 5); assert.equal(count('finish'), 0); break;
  case 'success': assert.equal(count('delete'), 1); assert.equal(saved, 'DELETED'); assert.equal(state.errors.length, 0); break;
}
if (!['five-attempt-limit', 'failed-delete-still-kicks', 'save-result-failure', 'start-forbidden', 'finish-failure'].includes(scenario))
  assert.equal(state.phase, 'solved');
console.log(`PASS ${scenario}`);
