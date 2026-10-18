#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT=$(cd "$SCRIPT_DIR/../.." && pwd)
export ASE_CATEGORY=mutation-testing
export ASE_REPORT_ROOT=${ASE_REPORT_ROOT:-${TMPDIR:-/tmp}/ase-testing-reports}
export ASE_REPORT_DIR="$ASE_REPORT_ROOT/$ASE_CATEGORY"

# Set true only after replacing run_tests below with real assessment commands.
ASE_SUITE_READY=true
if [[ "${1:-}" == --check-ready ]]; then
  [[ "$ASE_SUITE_READY" == true ]] && exit 0
  echo "$ASE_CATEGORY: assessment tests are not implemented yet."
  exit 78
fi

mkdir -p "$ASE_REPORT_DIR"
if [[ -e "$ASE_REPORT_DIR/status.txt" ]]; then
  echo "Results already exist at $ASE_REPORT_DIR. Choose a fresh ASE_REPORT_ROOT." >&2
  exit 2
fi
if [[ "$ASE_SUITE_READY" != true ]]; then
  echo not-implemented > "$ASE_REPORT_DIR/status.txt"
  printf '# %s\n\nAssessment tests are not implemented yet. No tests were run.\n\nPut the testing commands in this category\x27s run.sh and then set ASE_SUITE_READY=true.\n' \
    "$ASE_CATEGORY" > "$ASE_REPORT_DIR/summary.md"
  printf '{"cases":[]}\n' > "$ASE_REPORT_DIR/details.json"
  cat "$ASE_REPORT_DIR/summary.md" | tee "$ASE_REPORT_DIR/console.log"
  exit 78
fi

finish() {
  local result=$?
  trap - EXIT
  if (( result == 0 )); then
    bash "$SCRIPT_DIR/../shared/start-environment.sh" validate-report "$ASE_CATEGORY" || result=$?
  fi
  if (( result == 0 )); then
    echo passed > "$ASE_REPORT_DIR/status.txt"
  elif (( result == 78 )); then
    echo not-implemented > "$ASE_REPORT_DIR/status.txt"
  else
    echo failed > "$ASE_REPORT_DIR/status.txt"
  fi
  if [[ ! -s "$ASE_REPORT_DIR/summary.md" ]]; then
    printf '# %s\n\nExecution ended with exit code %s. Inspect console.log.\n' \
      "$ASE_CATEGORY" "$result" > "$ASE_REPORT_DIR/summary.md"
  fi
  cat "$ASE_REPORT_DIR/summary.md"
  exit "$result"
}

run_tests() {
  local stryker_out="$ASE_REPORT_DIR/stryker-report"

  # Services under mutation test, one Stryker.NET run each (Stryker mutates a
  # single project under test per run). Format:
  #   <config-basename>|<unit-test project path relative to testing-pipeline>
  local services=(
    "auth-service|AuthService.UnitTests/AuthService.UnitTests.csproj"
    "item-service|ItemService.UnitTests/ItemService.UnitTests.csproj"
    "matching-service|MatchingService.UnitTests/MatchingService.UnitTests.csproj"
  )

  echo "=== ASE Mutation Testing ==="
  echo "Scope:   AuthService, ItemService and MatchingService unit test stories"
  echo "Tool:    Stryker.NET (one run per service, results merged)"
  echo ""

  # --- Prerequisites ---
  command -v dotnet >/dev/null || { echo '.NET 8 SDK is required.' >&2; return 1; }
  command -v node >/dev/null || { echo 'Node.js is required for reporting.' >&2; return 1; }

  local report_args=()
  local run_failed=0

  for entry in "${services[@]}"; do
    local service="${entry%%|*}"
    local unit_project="$SCRIPT_DIR/../unit-testing/${entry##*|}"
    local out="$stryker_out/$service"

    echo "--- $service ---"
    echo "Unit tests: ${entry##*|}"

    # --- Restore and build the unit test project ---
    echo "Restoring unit test project..."
    dotnet restore "$unit_project" || return 1
    echo "Building unit test project..."
    dotnet build "$unit_project" -c Release --no-restore || return 1

    # --- Run Stryker.NET mutation analysis ---
    echo "Running Stryker.NET mutation analysis..."
    # Restore the pinned local tool manifest (.config/dotnet-tools.json) so a fresh
    # CI runner can resolve dotnet-stryker. No-op when already restored.
    ( cd "$SCRIPT_DIR" && dotnet tool restore ) || return 1

    # Report generation below decides pass/fail, so a non-zero Stryker exit must
    # not abort this function before the ASE reports are written.
    local stryker_exit=0
    ( cd "$SCRIPT_DIR" && dotnet tool run dotnet-stryker \
      --config-file "stryker-configs/$service.json" \
      --output "$out" \
      --verbosity info ) || stryker_exit=$?
    if [[ "$stryker_exit" != 0 ]]; then
      echo "Stryker.NET exited with $stryker_exit for $service." >&2
      run_failed=1
    fi

    # The JSON report may sit in a subdirectory such as reports/
    local stryker_json=""
    if [[ -d "$out" ]]; then
      stryker_json=$(find "$out" -name 'mutation-report.json' -type f 2>/dev/null | sort | head -1)
    fi
    if [[ -n "$stryker_json" && -f "$stryker_json" ]]; then
      report_args+=("$service=$stryker_json")
    else
      echo "No Stryker JSON report found in $out" >&2
      run_failed=1
    fi
    echo ""
  done

  # --- Generate the merged ASE report from every Stryker run ---
  echo "Generating ASE reports..."

  local report_exit=0
  if (( ${#report_args[@]} > 0 )); then
    node - "$ASE_REPORT_DIR" "${report_args[@]}" <<'JS' || report_exit=$?
const fs = require('node:fs');
const path = require('node:path');

const reportDir = process.argv[2];

// Each service is scored and gated on its own. breakAt is that service's
// measured baseline, so a strong service can never carry a weak one (ratchet
// rule: a score may not drop below the recorded baseline).
const SERVICES = {
  'auth-service': {
    title: 'AuthService',
    breakAt: 75,
    baselineNote: '81% measured across all AuthService stories',
    stories: 'Registration, Email verification, Login, Profile, Password reset,\n  Account deletion, Admin user management',
    sourceNote: '`AuthController`, `AdminController`, `PasswordValidator` and the\n  request/response DTOs their validation tests exercise',
    scope: new Set([
      'AuthController.cs',
      'AdminController.cs',
      'PasswordValidator.cs',
      'RegisterRequest.cs',
      'LoginRequest.cs',
      'LoginResponse.cs',
      'ConfirmRequest.cs',
      'VerifyEmailRequest.cs',
      'ResendVerificationRequest.cs',
      'ForgotPasswordRequest.cs',
      'ResetPasswordRequest.cs',
      'UpdateProfileRequest.cs',
      'UserProfileDto.cs',
      'DeleteAccountRequest.cs',
      'AdminUserDto.cs',
      'AdminUserContactDto.cs'
    ])
  },
  'item-service': {
    title: 'ItemService',
    breakAt: 60,
    baselineNote: '66% measured across the six item story folders',
    stories: 'Report lost item, Report found item, Edit item report,\n  Search and filter items, Mark item resolved, View item details',
    sourceNote: '`LostItemsController`, `FoundItemsController`, `ItemsController`\n  and the request/response DTOs their validation tests exercise',
    knownGaps: 'Known test gap: `PUT /{id}/photo` and `DELETE /{id}/photo` have no\n  unit tests, so their mutants are reported as no-coverage. Closing that gap\n  is tracked as a follow-up.',
    scope: new Set([
      'LostItemsController.cs',
      'FoundItemsController.cs',
      'ItemsController.cs',
      'ReportLostItemRequest.cs',
      'ReportFoundItemRequest.cs',
      'UpdateLostItemRequest.cs',
      'UpdateFoundItemRequest.cs',
      'ItemDetailDto.cs',
      'ItemSummaryDto.cs',
      'PagedResultDto.cs'
    ])
  },
  'matching-service': {
    title: 'MatchingService',
    breakAt: 60,
    baselineNote: '64% measured across the ten matching story folders',
    stories: 'Photo description pipeline, Preview and claim match,\n  View potential matches, Lost reporter decision, Finder decision,\n  Matching notifications, Match deactivation, Match re-evaluation,\n  Send match appeal, Admin match appeal review',
    sourceNote: 'the photo pipeline, claim, match read, notification and re-evaluation\n  services the stories drive, plus the decision and match appeal\n  controllers and the request/response models they validate',
    knownGaps: 'Known test gaps: claim paths in `ClaimService` beyond preview and\n  claim, `ImageDescriptionWorker` plumbing, `ClaimItemClient` error paths and\n  `MatchReevaluationEventHandler` branches have no unit tests, so those\n  mutants are reported as no-coverage. Closing them is tracked as a\n  follow-up.',
    scope: new Set([
      'ImageDescriptionWorker.cs',
      'ImageDescriptionValidator.cs',
      'BlobUrlPhotoKeyGenerator.cs',
      'ClaimService.cs',
      'ClaimItemClient.cs',
      'ClaimModels.cs',
      'MatchReadModels.cs',
      'MatchReadService.cs',
      'MatchQueriesController.cs',
      'FinderDecisionsController.cs',
      'LostReporterDecisionsController.cs',
      'MatchEmailSender.cs',
      'MatchReevaluationEventHandler.cs',
      'AppealService.cs',
      'AdminAppealService.cs',
      'AppealModels.cs',
      'AppealsController.cs',
      'AdminAppealsController.cs'
    ])
  }
};

const validStates = new Set(['killed', 'survived', 'timeout', 'no-coverage', 'compile-error', 'skipped']);

function scoreOf(stats) {
  const tested = stats.killed + stats.survived + stats.timeout;
  return {
    tested: tested,
    score: tested > 0 ? Math.round((stats.killed / tested) * 100) : 0,
    strictScore: tested + stats.noCoverage > 0
      ? Math.round((stats.killed / (tested + stats.noCoverage)) * 100)
      : 0
  };
}

const results = [];
const allCases = [];
const perFile = new Map();

for (const arg of process.argv.slice(3)) {
  const split = arg.indexOf('=');
  const serviceId = arg.slice(0, split);
  const jsonPath = arg.slice(split + 1);
  const service = SERVICES[serviceId];

  if (!service) {
    console.warn('Unknown service id in report argument: ' + serviceId);
    continue;
  }

  const stryker = JSON.parse(fs.readFileSync(jsonPath, 'utf8'));
  const stats = { total: 0, killed: 0, survived: 0, timeout: 0, noCoverage: 0, compileError: 0, skipped: 0 };

  for (const [filePath, fileData] of Object.entries(stryker.files || {})) {
    const shortName = filePath.split(/[/\\]/).pop();

    // Only report mutants from this service's story-scoped source files
    if (!service.scope.has(shortName)) continue;

    for (const m of fileData.mutants || []) {
      // Skip mutants removed by the mutate filter
      if (m.status === 'Ignored' && m.statusReason === 'Removed by mutate filter') continue;

      // Normalize Stryker.NET statuses (CamelCase, no separators) to ASE states
      let status = (m.status || 'survived').toLowerCase().replace(/[^a-z]/g, '');
      if (status === 'compileerror') status = 'compile-error';
      if (status === 'nocoverage') status = 'no-coverage';
      if (status === 'survived' && m.statusReason && m.statusReason.includes('compile error')) {
        status = 'compile-error';
      }
      if (status === 'ignored') status = 'skipped';
      if (!validStates.has(status)) status = 'survived';

      const startLine = m.location?.start?.line || 0;
      const endLine = m.location?.end?.line || startLine;
      const lineRef = startLine === endLine ? String(startLine) : startLine + '-' + endLine;

      const caseName = service.title + ': ' + (m.mutatorName || 'mutation') + ' @ ' + shortName + ':' + lineRef;
      allCases.push({
        name: caseName,
        description: (m.mutatorName || 'mutation') + ' mutation in ' + shortName + ':' + lineRef +
          ' - replaced ' + (m.replacement || '?') +
          (m.killedBy && m.killedBy.length > 0 ? '. Killed by tests: ' + m.killedBy.join(', ') : '') +
          (m.statusReason ? '. ' + m.statusReason : ''),
        status: status,
        durationMs: 0
      });

      stats.total++;
      if (status === 'killed') stats.killed++;
      if (status === 'survived') stats.survived++;
      if (status === 'no-coverage') stats.noCoverage++;
      if (status === 'compile-error') stats.compileError++;
      if (status === 'skipped') stats.skipped++;

      if (!perFile.has(caseName.split(' ')[0] + '|' + shortName)) {
        perFile.set(caseName.split(' ')[0] + '|' + shortName,
          { service: service.title, file: shortName, total: 0, killed: 0, survived: 0, noCoverage: 0 });
      }
      const row = perFile.get(caseName.split(' ')[0] + '|' + shortName);
      row.total++;
      if (status === 'killed') row.killed++;
      if (status === 'survived') row.survived++;
      if (status === 'no-coverage') row.noCoverage++;
    }
  }

  results.push({ service: service, stats: stats, scores: scoreOf(stats) });
}

fs.writeFileSync(
  path.join(reportDir, 'details.json'),
  JSON.stringify({ cases: allCases }, null, 2)
);

let md = '# Mutation Testing Report\n\n';
md += '## Scope\n';
md += 'Two services are mutation tested. Each one runs its own Stryker.NET run\n';
md += 'against its own unit test project, is scored on its own, and is gated on its\n';
md += 'own break-at threshold. Scores are never blended across services, so a\n';
md += 'strong service cannot carry a weak one.\n\n';
for (const r of results) {
  md += '### ' + r.service.title + '\n';
  md += '- Stories: ' + r.service.stories + '\n';
  md += '- Source: ' + r.service.sourceNote + '\n';
  md += '- Break-at threshold: ' + r.service.breakAt + ' (' + r.service.baselineNote + ')\n';
  if (r.service.knownGaps) md += '- ' + r.service.knownGaps + '\n';
  md += '\n';
}

md += '## Tool\nStryker.NET - mutation testing for .NET\n\n';
md += '## Configuration\n';
md += '- Mutation level: Standard\n';
md += '- One Stryker.NET run per service, results merged into this category report\n';
md += '- Gate: every service must clear its own break-at threshold. Thresholds are\n';
md += '  set from each service measured baseline, so a later score may not drop\n';
md += '  below it (ratchet); improvements only ever raise it\n';
md += '- Score definition: killed / (killed + survived + timeout); mutants in code\n';
md += '  paths no test reaches are reported as no-coverage test gaps\n\n';

md += '## Results by service\n\n';
md += '| Service | Mutants | Killed | Survived | No coverage | Score | Score with gaps | Break-at | Result |\n';
md += '| --- | --- | --- | --- | --- | --- | --- | --- | --- |\n';
let allPassed = results.length > 0;
for (const r of results) {
  const passed = r.scores.score >= r.service.breakAt;
  if (!passed) allPassed = false;
  md += '| ' + r.service.title + ' | ' + r.stats.total + ' | ' + r.stats.killed + ' | ' +
    r.stats.survived + ' | ' + r.stats.noCoverage + ' | ' + r.scores.score + '% | ' +
    r.scores.strictScore + '% | ' + r.service.breakAt + '% | ' + (passed ? 'PASSED' : 'BELOW THRESHOLD') + ' |\n';
}
md += '\n';

md += '## Totals\n';
const totals = results.reduce((acc, r) => {
  acc.total += r.stats.total;
  acc.killed += r.stats.killed;
  acc.survived += r.stats.survived;
  acc.noCoverage += r.stats.noCoverage;
  acc.compileError += r.stats.compileError;
  acc.skipped += r.stats.skipped;
  acc.tested += r.scores.tested;
  return acc;
}, { total: 0, killed: 0, survived: 0, noCoverage: 0, compileError: 0, skipped: 0, tested: 0 });
md += '- Total mutants (in scope): ' + totals.total + '\n';
md += '- Killed: ' + totals.killed + '\n';
md += '- Survived: ' + totals.survived + '\n';
md += '- No coverage (test gaps): ' + totals.noCoverage + '\n';
md += '- Compile errors: ' + totals.compileError + '\n';
md += '- Skipped: ' + totals.skipped + '\n';
md += '- Mutants tested: ' + totals.tested + '\n\n';

md += '## Results by file\n\n';
md += '| Service | Source file | Mutants | Killed | Survived | No coverage |\n';
md += '| --- | --- | --- | --- | --- | --- |\n';
for (const [, row] of [...perFile.entries()].sort((a, b) =>
  a[1].service.localeCompare(b[1].service) || a[1].file.localeCompare(b[1].file))) {
  md += '| ' + row.service + ' | `' + row.file + '` | ' + row.total + ' | ' + row.killed + ' | ' +
    row.survived + ' | ' + row.noCoverage + ' |\n';
}
md += '\n';

md += '## Outcome\n';
for (const r of results) {
  const passed = r.scores.score >= r.service.breakAt;
  md += '- ' + r.service.title + ': ' + (passed ? 'PASSED' : 'BELOW THRESHOLD') + ' - score ' +
    r.scores.score + '% against break-at ' + r.service.breakAt + '%\n';
}
md += allPassed
  ? '\nPASSED - every service cleared its own break-at threshold.\n'
  : '\nFAILED - at least one service is below its break-at threshold.\n';

fs.writeFileSync(path.join(reportDir, 'summary.md'), md);
console.log('Wrote summary.md and details.json to ' + reportDir);
process.exit(allPassed ? 0 : 1);
JS
  else
    echo 'No Stryker JSON report was produced for any service.' >&2
    printf '# Mutation Testing Report\n\nNo Stryker JSON report was generated. Inspect console.log.\n' > "$ASE_REPORT_DIR/summary.md"
    printf '{"cases":[]}\n' > "$ASE_REPORT_DIR/details.json"
    report_exit=1
  fi

  # --- Display summary ---
  if [[ -f "$ASE_REPORT_DIR/summary.md" ]]; then
    echo ''
    echo '--- Summary ---'
    cat "$ASE_REPORT_DIR/summary.md"
  fi

  # A failed Stryker run outranks the score gate.
  if [[ "$run_failed" != 0 ]]; then
    return 1
  fi
  return "$report_exit"
}

echo running > "$ASE_REPORT_DIR/status.txt"
trap finish EXIT
exec > >(tee "$ASE_REPORT_DIR/console.log") 2>&1
cd "$ROOT"
run_tests "$@"