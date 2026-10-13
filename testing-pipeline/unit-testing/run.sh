#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT=$(cd "$SCRIPT_DIR/../.." && pwd)
export ASE_CATEGORY=unit-testing
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
    bash "$SCRIPT_DIR/../shared/start-environment.sh" validate-report "$ASE_CATEGORY" \
      >> "$ASE_REPORT_DIR/console.log" 2>&1 || result=$?
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
  if (( result != 0 )); then
    echo "Unit run failed (exit $result). Full diagnostics are in console.log."
  fi
  exit "$result"
}

run_tests() {
  command -v dotnet >/dev/null || { echo '.NET 8 SDK is required.' >&2; return 1; }
  command -v node >/dev/null || { echo 'Node.js is required for unit reporting.' >&2; return 1; }
  local service='' story='' dotnet_args=() projects=()
  while (( $# > 0 )); do
    case "$1" in
      --service|--story)
        (( $# >= 2 )) || { echo "$1 needs a name." >&2; return 2; }
        [[ "$2" =~ ^[A-Za-z][A-Za-z0-9_]*$ ]] || { echo "Invalid service/story name: $2" >&2; return 2; }
        if [[ "$1" == --service ]]; then service=$2; else story=$2; fi
        shift 2
        ;;
      *) dotnet_args+=("$1"); shift ;;
    esac
  done
  [[ -z "$story" || -n "$service" ]] || { echo '--story requires --service.' >&2; return 2; }
  if [[ -n "$service" ]]; then
    local project_path="$SCRIPT_DIR/$service.UnitTests/$service.UnitTests.csproj"
    [[ -f "$project_path" ]] || { echo "No assessment unit-test project exists for $service." >&2; return 2; }
    projects=("$project_path")
    if [[ -n "$story" ]]; then
      [[ -d "$SCRIPT_DIR/$service.UnitTests/$story" ]] || { echo "No story folder $story exists for $service." >&2; return 2; }
      dotnet_args+=(--filter "FullyQualifiedName~$service.UnitTests.$story.")
    fi
  else
    mapfile -t projects < <(find "$SCRIPT_DIR" -maxdepth 2 -name '*.csproj' -type f | sort)
  fi
  (( ${#projects[@]} > 0 )) || { echo 'No assessment unit-test projects were found.' >&2; return 1; }
  local selection="${service:-All available services}${story:+ / $story}"
  echo "Running unit tests: $selection"
  local suite_started=$SECONDS
  local project name result test_result=0 report_result=0
  for project in "${projects[@]}"; do
    name=$(basename "$project" .csproj)
    result=0
    dotnet test "$project" -c Release \
      --logger 'console;verbosity=detailed' --logger "trx;LogFileName=$name.trx" \
      --results-directory "$ASE_REPORT_DIR/results" "${dotnet_args[@]}" \
      >> "$ASE_REPORT_DIR/console.log" 2>&1 || result=$?
    if (( result != 0 )); then test_result=$result; fi
  done
  # Generate available reports even if compilation or a test assertion failed.
  node "$SCRIPT_DIR/scripts/summarize-results.mjs" "$ASE_REPORT_DIR/results" "$ASE_REPORT_DIR" || report_result=$?
  local suite_elapsed=$((SECONDS - suite_started))
  echo "Unit suite wall-clock time: ${suite_elapsed}s (selected projects and reporting)."
  if [[ -s "$ASE_REPORT_DIR/summary.md" ]]; then
    printf '\nSelected scope: %s\n' "$selection" >> "$ASE_REPORT_DIR/summary.md"
    printf '\nWall-clock duration: %ss (selected projects and reporting; includes restore/build unless --no-build --no-restore).\n' "$suite_elapsed" >> "$ASE_REPORT_DIR/summary.md"
  fi
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" && -s "$ASE_REPORT_DIR/summary.md" ]]; then
    cat "$ASE_REPORT_DIR/summary.md" >> "$GITHUB_STEP_SUMMARY"
  fi
  if (( test_result != 0 )); then return "$test_result"; fi
  return "$report_result"
}

echo running > "$ASE_REPORT_DIR/status.txt"
trap finish EXIT
exec > >(tee -a "$ASE_REPORT_DIR/console.log") 2>&1
cd "$ROOT"
run_tests "$@"
