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
  local unit_project="$SCRIPT_DIR/../unit-testing/AuthService.UnitTests/AuthService.UnitTests.csproj"
  local stryker_out="$ASE_REPORT_DIR/stryker-report"

  echo "=== ASE Mutation Testing ==="
  echo "Scope:   Registration story tests (AuthService)"
  echo "Source:  AuthController.Register, PasswordValidator, RegisterRequest DTO"
  echo "Tests:   AuthService.UnitTests (Registration story)"
  echo "Tool:    Stryker.NET"
  echo ""

  # --- Prerequisites ---
  command -v dotnet >/dev/null || { echo '.NET 8 SDK is required.' >&2; return 1; }
  command -v node >/dev/null || { echo 'Node.js is required for reporting.' >&2; return 1; }

  # --- Restore and build the unit test project ---
  echo "Restoring unit test project..."
  dotnet restore "$unit_project" || return 1
  echo "Building unit test project..."
  dotnet build "$unit_project" -c Release --no-restore || return 1

  # --- Run Stryker.NET mutation analysis ---
  echo "Running Stryker.NET mutation analysis..."
  cd "$SCRIPT_DIR"

  dotnet tool run dotnet-stryker \
    --config-file "stryker-config.json" \
    --output "$stryker_out" \
    --verbosity info

  local stryker_exit=$?

  # --- Generate ASE shared reports from Stryker output ---
  echo "Generating ASE reports..."

  # Find the Stryker JSON report (may be in a subdirectory like reports/)
  local stryker_json=""
  if [[ -d "$stryker_out" ]]; then
    stryker_json=$(find "$stryker_out" -name 'mutation-report.json' -type f 2>/dev/null | sort | head -1)
  fi

  if [[ -n "$stryker_json" && -f "$stryker_json" ]]; then
    node - "$stryker_json" "$ASE_REPORT_DIR" <<'JS'
const fs = require('node:fs');
const path = require('node:path');

const strykerJsonPath = process.argv[2];
const reportDir = process.argv[3];

// Registration-scoped source files we care about
const SCOPED_FILES = new Set(['AuthController.cs', 'PasswordValidator.cs', 'RegisterRequest.cs']);

const stryker = JSON.parse(fs.readFileSync(strykerJsonPath, 'utf8'));

const validStates = new Set(['killed', 'survived', 'timeout', 'no-coverage', 'compile-error', 'skipped']);

const cases = [];
const fileEntries = Object.entries(stryker.files || {});

for (const [filePath, fileData] of fileEntries) {
  const shortName = filePath.split(/[/\\]/).pop();

  // Only report mutants from Registration-scoped source files
  if (!SCOPED_FILES.has(shortName)) continue;

  const mutants = fileData.mutants || [];

  for (const m of mutants) {
    // Skip mutants removed by the mutate filter
    if (m.status === 'Ignored' && m.statusReason === 'Removed by mutate filter') continue;

    let status = (m.status || 'survived').toLowerCase();
    // Map Stryker.NET statuses to ASE valid states
    if (status === 'compileerror') status = 'compile-error';
    if (status === 'survived' && m.statusReason && m.statusReason.includes('compile error')) {
      status = 'compile-error';
    }
    if (status === 'ignored') status = 'skipped';
    if (!validStates.has(status)) status = 'survived';

    const startLine = m.location?.start?.line || 0;
    const endLine = m.location?.end?.line || startLine;
    const lineRef = startLine === endLine ? String(startLine) : startLine + '-' + endLine;

    cases.push({
      name: (m.mutatorName || 'mutation') + ' @ ' + shortName + ':' + lineRef,
      description: (m.mutatorName || 'mutation') + ' mutation in ' + shortName + ':' + lineRef +
        ' — replaced ' + (m.replacement || '?') +
        (m.killedBy && m.killedBy.length > 0 ? '. Killed by tests: ' + m.killedBy.join(', ') : '') +
        (m.statusReason ? '. ' + m.statusReason : ''),
      status: status,
      durationMs: 0
    });
  }
}

fs.writeFileSync(
  path.join(reportDir, 'details.json'),
  JSON.stringify({ cases: cases }, null, 2)
);

const killed = cases.filter(c => c.status === 'killed').length;
const survived = cases.filter(c => c.status === 'survived').length;
const compileError = cases.filter(c => c.status === 'compile-error').length;
const timeout = cases.filter(c => c.status === 'timeout').length;
const skipped = cases.filter(c => c.status === 'skipped').length;
const tested = killed + survived + timeout;
const total = cases.length;
const score = tested > 0 ? Math.round((killed / tested) * 100) : 0;

let md = '# Mutation Testing Report\n\n';
md += '## Scope\n';
md += 'Registration story tests (AuthService):\n';
md += '- `AuthController.Register` (controller method)\n';
md += '- `PasswordValidator` (password complexity rules)\n';
md += '- `RegisterRequest` (DTO validation)\n\n';
md += '## Tool\nStryker.NET — mutation testing for .NET\n\n';
md += '## Configuration\n';
md += '- Mutation level: Standard\n';
md += '- Break-at threshold: 70 (exit non-zero below this)\n';
md += '- Threshold low: 70, high: 80\n\n';
md += '## Results\n';
md += '- Total mutants (in scope): ' + total + '\n';
md += '- Killed: ' + killed + '\n';
md += '- Survived: ' + survived + '\n';
md += '- Compile errors: ' + compileError + '\n';
md += '- Timeout: ' + timeout + '\n';
md += '- Skipped: ' + skipped + '\n';
md += '- Mutation score (tested only): ' + score + '%\n\n';

if (score >= 70) {
  md += '## Outcome: PASSED\nMutation score ' + score + '% meets the break-at threshold of 70.\n';
} else {
  md += '## Outcome: BELOW THRESHOLD\nMutation score ' + score + '% is below the break-at threshold of 70.\n';
}

fs.writeFileSync(path.join(reportDir, 'summary.md'), md);
console.log('Wrote summary.md and details.json to ' + reportDir);
JS
  else
    echo 'No Stryker JSON report found in ' "$stryker_out" >&2
    printf '# Mutation Testing Report\n\nNo Stryker JSON report was generated. Inspect console.log.\n' > "$ASE_REPORT_DIR/summary.md"
    printf '{"cases":[]}\n' > "$ASE_REPORT_DIR/details.json"
  fi

  # --- Display summary ---
  if [[ -f "$ASE_REPORT_DIR/summary.md" ]]; then
    echo ''
    echo '--- Summary ---'
    cat "$ASE_REPORT_DIR/summary.md"
  fi

  return $stryker_exit
}

echo running > "$ASE_REPORT_DIR/status.txt"
trap finish EXIT
exec > >(tee "$ASE_REPORT_DIR/console.log") 2>&1
cd "$ROOT"
run_tests "$@"
