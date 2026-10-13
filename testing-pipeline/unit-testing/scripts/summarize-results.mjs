// Unit-specific reporting. Reads the standard VSTest TRX files produced by run.sh.
// No browser, Python, or report-generator package is needed.
// Usage: node summarize-results.mjs <trx-directory> <output-directory>
import fs from 'node:fs';
import path from 'node:path';

const [inputDir, outputDir] = process.argv.slice(2);
if (!inputDir || !outputDir) {
  console.error('Usage: node summarize-results.mjs <trx-directory> <output-directory>');
  process.exit(2);
}
fs.mkdirSync(outputDir, { recursive: true });

const descriptions = {
  Valid_registration_creates_unverified_user_and_requests_verification_email:
    'Creates an unverified non-admin user using the returned password hash; stores a hashed OTP with expiry and requests verification email/session creation through mocks.',
  Duplicate_email_rejects_registration_without_side_effects:
    'Rejects an existing email before checking phone or creating a user; verifies hashing, OTP, email and session dependencies are untouched.',
  Duplicate_phone_rejects_registration_without_side_effects:
    'Rejects an existing phone number after the email check without creating a user, hashing a password or sending email.',
  Weak_password_returns_requirements_before_repository_access:
    'Returns password requirements for weak input before accessing repositories or performing registration side effects.',
  Password_rules_reject_invalid_inputs_and_accept_length_boundaries:
    'Checks the real password validator for the named complexity rule or the minimum/maximum permitted length.',
  Invalid_phone_formats_produce_phone_validation_error:
    'Checks the real registration DTO rejects the supplied invalid phone format and identifies the phone field.',
  Each_required_field_rejects_empty_input:
    'Checks the real registration DTO reports a required-field error when the named field is empty.',
  Correct_otp_marks_account_verified_consumes_code_and_issues_auth_cookie:
    'Verifies account/code updates, verification event payload, optional email change, returned token and environment-specific auth cookie using strict mocks.',
  Consumed_otp_cannot_verify_again_when_repository_returns_no_active_code:
    'After consumption, stubs the repository returning no active code; a retry cannot repeat verification, publication or JWT issuance. Does not test SQL filtering.',
  Incorrect_otp_increments_attempts_and_locks_at_configured_limit:
    'Checks retry and lockout boundaries with a ten-attempt configuration; wrong codes increment once and never verify the account.',
  No_active_otp_returns_invalid_or_expired_error_without_verification:
    'Stubs the repository returning no active OTP and checks the error with no verification side effects; expiry filtering itself belongs to integration tests.',
  Invalid_verification_session_rejects_before_repository_access:
    'An invalid verification session rejects before accessing account/code repositories or issuing authentication.',
  Verified_or_suspended_account_rejects_before_otp_access:
    'Already-verified or suspended accounts return their current errors without processing an OTP.',
  Pending_email_claimed_by_another_account_consumes_code_without_verifying:
    'An email claimed since OTP issuance invalidates the code without changing or verifying the account.',
  Allowed_resend_invalidates_old_codes_stores_hash_and_emails_new_code:
    'Checks invalidation before generation, hashed OTP storage, configured expiry, email content and resend timestamp; includes cooldown elapsed and changed email.',
  Resend_inside_cooldown_returns_retry_after_without_side_effects:
    'Checks HTTP 429 and Retry-After for recent resend timestamps, with no new OTP or email and no real waiting.',
  Invalid_resend_rejects_without_creating_or_sending_code:
    'Invalid sessions, missing/suspended accounts or duplicate pending emails reject without OTP/email side effects.',
  Otp_validation_requires_exactly_six_digits:
    'Exercises real DTO validation for missing, malformed and valid six-digit OTPs, including leading zeroes.',
  Verification_requires_session_token:
    'Checks that verification DTO validation identifies a missing session token.',
  Resend_validation_requires_session_and_valid_email:
    'Checks resend DTO validation identifies the missing session or missing/malformed email.',
  Verification_session_validates_signature_identity_and_lifetime:
    'Exercises the real session validator with valid, expired, wrong-key/issuer/audience, invalid-subject and malformed tokens; no sleep or network.',
  Valid_credentials_return_user_or_admin_details_and_auth_cookie:
    'Checks password verification against the stored hash, exact returned profile/role, JWT issuance, cookie security options and cancellation propagation.',
  Invalid_credentials_or_filtered_deleted_account_return_same_error_without_token:
    'Unknown email, incorrect password and repository-filtered deleted account return the same generic error with no token/cookie.',
  Unverified_login_returns_forbidden_and_sends_new_verification_code_without_login_jwt:
    'Documents current behaviour: HTTP 403 plus verification session, old-code invalidation and emailed replacement OTP, without a login JWT.',
  Suspended_verified_account_rejects_login_without_token:
    'Correct credentials on a suspended verified account return HTTP 403 without issuing authentication.',
  Signed_login_jwt_has_correct_identity_role_verification_and_expiry:
    'Validates a real HS256 signature, issuer, audience and lifetime, then checks identity, contact, admin and verification claims.',
  Login_jwt_is_rejected_when_validated_with_wrong_signing_key:
    'A real issued login JWT fails cryptographic validation with a different signing key.',
  Admin_policy_only_allows_existing_active_admin:
    'Runs the real AdminOnly handler against account/identity cases and checks AdminController uses that policy; HTTP middleware is outside this unit test.',
  View_profile_uses_authenticated_identity_and_returns_contact_details:
    'Checks profile lookup uses sub or NameIdentifier from the caller and returns exact contact details plus a refreshed JWT.',
  Profile_update_persists_callers_record_returns_reload_and_publishes_only_changed_fields:
    'Checks caller-owned lookup, uniqueness check, save/reload order and exact changed-field event payload for name, phone, both or unchanged input.',
  Duplicate_profile_phone_rejects_without_saving_or_publishing:
    'A phone belonging to another user returns HTTP 409 without profile persistence or event publication.',
  Missing_or_invalid_profile_identity_rejects_before_repository_access:
    'Both view and update reject missing/invalid caller identity before repository access or writes.',
  Missing_profile_account_rejects_without_saving:
    'Both view and update return HTTP 401 for a caller account absent from the repository, without writes.',
  Ineligible_account_cannot_view_profile:
    'Suspended or unverified accounts receive the current profile-view rejection without token issuance.',
  Profile_name_validation_checks_required_and_length_boundaries:
    'Real DTO validation checks empty/whitespace names, accepted lengths 1 and 150, and rejection at 151 characters.',
  Profile_phone_validation_checks_e164_format_and_length_boundaries:
    'Real DTO validation checks required input, leading plus, initial digit, numeric format and minimum/maximum E.164 lengths.'
};

function decode(value = '') {
  return value.replace(/&(#x[0-9a-f]+|#\d+|amp|lt|gt|quot|apos);/gi, (_, entity) => {
    if (entity.startsWith('#x')) return String.fromCodePoint(parseInt(entity.slice(2), 16));
    if (entity.startsWith('#')) return String.fromCodePoint(Number(entity.slice(1)));
    return { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" }[entity];
  });
}
function attributes(text) {
  return Object.fromEntries([...text.matchAll(/([\w:.-]+)\s*=\s*(?:"([^"]*)"|'([^']*)')/g)]
    .map(match => [match[1], decode(match[2] ?? match[3])]));
}
function elementText(body, tag) {
  const content = body.match(new RegExp(`<${tag}\\b[^>]*>([\\s\\S]*?)<\\/${tag}>`))?.[1] ?? '';
  return decode(content.replace(/<!\[CDATA\[([\s\S]*?)\]\]>/g, '$1')).trim();
}
function durationMs(value) {
  const match = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(value ?? '');
  if (!match) throw new Error(`Invalid TRX duration: ${value}`);
  return Math.round((Number(match[1] ?? 0) * 86400 + Number(match[2]) * 3600 + Number(match[3]) * 60 + Number(match[4])) * 1000);
}
function trxFiles(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const file = path.join(directory, entry.name);
    return entry.isDirectory() ? trxFiles(file) : entry.name.endsWith('.trx') ? [file] : [];
  }).sort();
}
function readTrx(file) {
  const xml = fs.readFileSync(file, 'utf8');
  // TRX is tool-generated XML with a default namespace. Read only its known
  // result/definition elements, and cross-check counters to reject partial reads.
  if (!/<TestRun\b/.test(xml) || !/<\/TestRun>\s*$/.test(xml)) throw new Error(`Incomplete TRX: ${file}`);
  const definitions = new Map();
  for (const match of xml.matchAll(/<UnitTest\b([^>]*)>([\s\S]*?)<\/UnitTest>/g)) {
    const test = attributes(match[1]);
    const method = attributes(match[2].match(/<TestMethod\b([^>]*)\/?\s*>/)?.[1] ?? '');
    definitions.set(test.id, method);
  }
  const results = [];
  for (const match of xml.matchAll(/<UnitTestResult\b([^>]*?)(?:\/>|>([\s\S]*?)<\/UnitTestResult>)/g)) {
    const result = attributes(match[1]);
    const method = definitions.get(result.testId);
    if (!method?.name || !result.testName) throw new Error(`Missing test definition in ${file}`);
    const status = result.outcome === 'Passed' ? 'passed'
      : ['NotExecuted', 'Skipped'].includes(result.outcome) ? 'skipped' : 'failed';
    results.push({
      name: result.testName,
      description: descriptions[method.name] ?? `Checks ${method.name.replaceAll('_', ' ')}.`,
      status,
      durationMs: durationMs(result.duration ?? '00:00:00'),
      service: method.className?.split('.')[0] ?? 'Unknown',
      testClass: method.className,
      ...(status === 'failed' ? { error: elementText(match[2] ?? '', 'Message') || `TRX outcome: ${result.outcome}` } : {})
    });
  }
  const counters = attributes(xml.match(/<Counters\b([^>]*)\/?\s*>/)?.[1] ?? '');
  if (results.length === 0 || Number(counters.total) !== results.length) {
    throw new Error(`Missing/inconsistent test results in ${file}`);
  }
  const failed = results.filter(result => result.status === 'failed').length;
  if (Number(counters.failed) > failed || Number(counters.error ?? 0) > 0 || Number(counters.timeout ?? 0) > 0) {
    throw new Error(`TRX reports additional execution failures in ${file}`);
  }
  const summaryOutcome = attributes(xml.match(/<ResultSummary\b([^>]*)>/)?.[1] ?? '').outcome;
  if (!['Completed', 'Passed'].includes(summaryOutcome) && failed === 0) {
    throw new Error(`Unsuccessful TRX run: ${summaryOutcome ?? 'missing outcome'}`);
  }
  return results;
}

let cases = [];
try {
  const files = trxFiles(inputDir);
  if (files.length === 0) throw new Error('No TRX files were produced. Inspect console.log for build/test failures.');
  for (const file of files) cases.push(...readTrx(file));
  if (!cases.some(test => test.status !== 'skipped')) throw new Error('No tests executed; every recorded case was skipped.');
} catch (error) {
  cases.push({ name: 'Unit-test execution evidence', description: 'Requires complete result files containing actual executed tests.', status: 'failed', error: error.message });
}
const counts = Object.fromEntries(['passed', 'failed', 'skipped'].map(status => [status, cases.filter(test => test.status === status).length]));
const escape = value => String(value ?? '').replaceAll('|', '\\|').replace(/\r?\n/g, ' ');
const summary = [
  '# Unit testing', '',
  `**Result: ${counts.failed ? 'FAILED' : 'PASSED'}**`, '',
  `Recorded cases: ${cases.length}; passed: ${counts.passed}; failed: ${counts.failed}; skipped: ${counts.skipped}.`,
  'Timing below sums individual test durations; use console output for total build/execution time.',
  `Total case duration: ${cases.reduce((sum, test) => sum + (test.durationMs ?? 0), 0)} ms.`, '',
  '| Test / parameters | What it verifies | Result | Duration ms |',
  '|---|---|---|---|',
  ...cases.map(test => `| ${escape(test.name)} | ${escape(test.description)} | ${test.status} | ${test.durationMs ?? '—'} |`),
  '',
  ...(counts.failed ? ['## Failures', '', ...cases.filter(test => test.status === 'failed').map(test => `- **${escape(test.name)}:** ${escape(test.error)}`), ''] : []),
  'Native results: `results/*.trx`. External dependencies are mocked; these are backend unit tests, not browser or database integration tests.', ''
].join('\n');
fs.writeFileSync(path.join(outputDir, 'summary.md'), summary);
fs.writeFileSync(path.join(outputDir, 'details.json'), JSON.stringify({ cases }, null, 2) + '\n');
// Keep the terminal readable; full names, descriptions and failures stay in reports.
function consoleName(test) {
  const name = test.name;
  if (name.includes('Password_rules_reject_invalid_inputs_and_accept_length_boundaries')) {
    const scenario = name.match(/scenario:\s*"([^"]+)"/)?.[1];
    const labels = {
      TooShort: 'Too short rejected', TooLong: 'Too long rejected',
      NoUppercase: 'Missing uppercase rejected', NoLowercase: 'Missing lowercase rejected',
      NoDigit: 'Missing digit rejected', MinimumLength: 'Minimum length accepted',
      MaximumLength: 'Maximum length accepted'
    };
    return `Password: ${labels[scenario] ?? scenario ?? 'rule check'}`;
  }
  if (name.includes('Each_required_field_rejects_empty_input')) {
    return `Empty ${name.match(/field:\s*"([^"]+)"/)?.[1] ?? 'required field'} rejected`;
  }
  if (name.includes('Invalid_phone_formats_produce_phone_validation_error')) {
    return `Invalid phone rejected: ${name.match(/phone:\s*"([^"]+)"/)?.[1] ?? 'format check'}`;
  }
  const labels = {
    Valid_registration_creates_unverified_user_and_requests_verification_email: 'Successful registration',
    Duplicate_email_rejects_registration_without_side_effects: 'Duplicate email rejected',
    Duplicate_phone_rejects_registration_without_side_effects: 'Duplicate phone rejected',
    Weak_password_returns_requirements_before_repository_access: 'Weak password rejected before storage'
  };
  const method = name.split('(')[0].split('.').at(-1);
  const parameters = name.includes('(') ? ` ${name.slice(name.indexOf('('))}` : '';
  return labels[method] ?? `${method.replaceAll('_', ' ')}${parameters}`;
}
let heading = '';
for (const test of [...cases].sort((a, b) => a.name.localeCompare(b.name))) {
  const story = test.testClass?.split('.')[2];
  const nextHeading = test.service ? `${test.service}${story ? ` — ${story}` : ''}` : 'Execution check';
  if (nextHeading !== heading) {
    console.log(`\n${nextHeading}`);
    heading = nextHeading;
  }
  const status = { passed: 'PASS', failed: 'FAIL', skipped: 'SKIP' }[test.status];
  console.log(`${status}  ${consoleName(test)}${test.durationMs !== undefined ? ` (${test.durationMs} ms)` : ''}`);
  if (test.status === 'failed') {
    console.log(`      ${String(test.error).replaceAll(inputDir, 'results').replace(/\s+/g, ' ').slice(0, 240)}`);
  }
}
console.log(`\n${counts.passed} passed | ${counts.failed} failed | ${counts.skipped} skipped`);
process.exitCode = counts.failed ? 1 : 0;
