#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT=$(cd "$SCRIPT_DIR/../.." && pwd)
export ASE_CATEGORY=ui-e2e-testing
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
  command -v dotnet >/dev/null || { echo '.NET 8 SDK is required.' >&2; return 1; }
  command -v node >/dev/null || { echo 'Node.js is required for report conversion.' >&2; return 1; }
  local project="$SCRIPT_DIR/LostFound.E2E.Tests/LostFound.E2E.Tests.csproj"
  [[ -f "$project" ]] || { echo "E2E project not found: $project" >&2; return 1; }

  # Headless by default in the pipeline; override with E2E_HEADLESS=false for a visible browser.
  export E2E_HEADLESS=${E2E_HEADLESS:-true}
  export E2E_SCREENSHOT_DIR="$ASE_REPORT_DIR/screenshots"
  mkdir -p "$ASE_REPORT_DIR/results" "$E2E_SCREENSHOT_DIR"

  # Extra arguments are passed to dotnet test, e.g. --filter "Category=Demo".
  echo "Running UI/E2E tests (headless=$E2E_HEADLESS) ${*:-}"
  local suite_started=$SECONDS test_result=0 report_result=0
  dotnet test "$project" -c Release \
    --logger 'console;verbosity=normal' --logger "trx;LogFileName=ui-e2e.trx" \
    --results-directory "$ASE_REPORT_DIR/results" "$@" || test_result=$?

  # Build summary.md and details.json even when tests failed, so the evidence is always there.
  node "$SCRIPT_DIR/scripts/trx-to-report.mjs" \
    "$ASE_REPORT_DIR/results/ui-e2e.trx" "$SCRIPT_DIR/LostFound.E2E.Tests/Tests" "$ASE_REPORT_DIR" || report_result=$?

  local elapsed=$((SECONDS - suite_started))
  echo "UI/E2E wall-clock time: ${elapsed}s."
  [[ -s "$ASE_REPORT_DIR/summary.md" ]] && printf '\nWall-clock duration: %ss.\n' "$elapsed" >> "$ASE_REPORT_DIR/summary.md"
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" && -s "$ASE_REPORT_DIR/summary.md" ]]; then
    cat "$ASE_REPORT_DIR/summary.md" >> "$GITHUB_STEP_SUMMARY"
  fi
  if (( test_result != 0 )); then return "$test_result"; fi
  return "$report_result"
}

echo running > "$ASE_REPORT_DIR/status.txt"
trap finish EXIT
exec > >(tee "$ASE_REPORT_DIR/console.log") 2>&1
cd "$ROOT"
run_tests "$@"
