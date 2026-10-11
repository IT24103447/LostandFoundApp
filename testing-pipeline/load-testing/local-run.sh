#!/usr/bin/env bash
# Local convenience wrapper for load testing (CI does not use it; run.sh stays the only test entry point).
#   start  fresh environment + load-testing seed, remembered for later commands
#   run    load tests against the remembered environment with a fresh report folder
#   stop   stop the remembered environment
#   all    start, run and stop in one go
set -euo pipefail
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT=$(cd "$SCRIPT_DIR/../.." && pwd)
SHARED="$ROOT/testing-pipeline/shared"
STATE_FILE=${ASE_LOAD_STATE_FILE:-${TMPDIR:-/tmp}/ase-load-testing-environment}
export ASE_COMPOSE_PROJECT=${ASE_COMPOSE_PROJECT:-ase-testing-local}

recorded_runtime() {
  [[ -f "$STATE_FILE" ]] && cat "$STATE_FILE"
}

load_environment() {
  local runtime
  runtime=$(recorded_runtime) || { echo "No environment recorded. Run: $0 start" >&2; return 1; }
  [[ -f "$runtime/environment.sh" ]] || { echo "Recorded environment is incomplete: $runtime. Run: $0 stop, then $0 start" >&2; return 1; }
  export ASE_RUNTIME_DIR=$runtime
  # environment.sh uses "declare -x", which would make the variables local to this function.
  # shellcheck source=/dev/null
  source <(sed 's/^declare -x /export /' "$ASE_RUNTIME_DIR/environment.sh")
}

start() {
  local runtime
  if runtime=$(recorded_runtime) && [[ -f "$runtime/compose.started" ]]; then
    echo "An environment is already running ($runtime). Run: $0 stop" >&2
    return 1
  fi
  export ASE_RUNTIME_DIR
  ASE_RUNTIME_DIR=$(mktemp -d -t ase-environment.XXXXXX)
  bash "$SHARED/start-environment.sh" start
  echo "$ASE_RUNTIME_DIR" > "$STATE_FILE"
  # start-environment.sh cleans up its own startup failures; anything failing after it must stop the environment too.
  if ! { load_environment && bash "$SHARED/seed-test-data.sh" load-testing; }; then
    echo "Seeding failed; stopping the environment that was just started." >&2
    stop
    return 1
  fi
  echo "Environment ready and seeded. Run: $0 run"
}

run() {
  load_environment
  # Set after sourcing: environment.sh carries the report root from start time.
  # ASE_LOCAL_REPORTS (optional) keeps each run in its own dated folder there instead of a temporary one.
  export ASE_REPORT_ROOT
  if [[ -n "${ASE_LOCAL_REPORTS:-}" ]]; then
    local base=$ASE_LOCAL_REPORTS
    [[ "$base" == /* ]] || base="$ROOT/$base"
    ASE_REPORT_ROOT="$base/load-report-$(date +%Y-%m-%d_%H%M%S)"
    mkdir -p "$ASE_REPORT_ROOT"
  else
    ASE_REPORT_ROOT=$(mktemp -d -t ase-reports.XXXXXX)
  fi
  local result=0 report="$ASE_REPORT_ROOT/load-testing"
  bash "$SCRIPT_DIR/run.sh" "$@" || result=$?
  echo "Reports: $report"
  if [[ -f "$report/html/index.html" ]]; then
    echo "Dashboard: $report/html/index.html"
    if [[ "${ASE_OPEN_REPORT:-true}" == true ]] && command -v explorer.exe >/dev/null && command -v wslpath >/dev/null; then
      explorer.exe "$(wslpath -w "$report/html/index.html")" || true
    fi
  fi
  return "$result"
}

stop() {
  local runtime
  runtime=$(recorded_runtime) || { echo "No environment recorded; nothing to stop."; return 0; }
  ASE_RUNTIME_DIR=$runtime bash "$SHARED/start-environment.sh" stop
  rm -f -- "$STATE_FILE"
  echo "Environment stopped. Logs kept in $runtime/logs"
}

all() {
  local result=0
  start
  run "$@" || result=$?
  stop
  return "$result"
}

command=${1:-}
shift || true
case "$command" in
  start) start ;;
  run) run "$@" ;;
  stop) stop ;;
  all) all "$@" ;;
  *) echo "Usage: bash testing-pipeline/load-testing/local-run.sh start|run|stop|all" >&2; exit 2 ;;
esac
