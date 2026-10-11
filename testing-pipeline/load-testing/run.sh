#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT=$(cd "$SCRIPT_DIR/../.." && pwd)
export ASE_CATEGORY=load-testing
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

JMETER_VERSION=5.6.3
JMETER_CACHE=${ASE_JMETER_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/ase-load-testing}
JMETER="$JMETER_CACHE/apache-jmeter-$JMETER_VERSION/bin/jmeter"

install_jmeter() {
  command -v java >/dev/null || { echo 'Java 17 or newer is required for JMeter.' >&2; return 1; }
  [[ -x "$JMETER" ]] && return 0
  local archive="apache-jmeter-$JMETER_VERSION.tgz"
  local url="https://archive.apache.org/dist/jmeter/binaries/$archive"
  echo "Installing JMeter $JMETER_VERSION into $JMETER_CACHE"
  mkdir -p "$JMETER_CACHE"
  (
    cd "$JMETER_CACHE"
    curl --fail --silent --show-error --location --retry 3 -o "$archive" "$url"
    curl --fail --silent --show-error --location --retry 3 -o "$archive.sha512" "$url.sha512"
    sha512sum -c "$archive.sha512"
    tar -xzf "$archive"
    rm -f "$archive" "$archive.sha512"
  )
}

host_of() { local u=${1#*://}; echo "${u%%[:/]*}"; }
port_of() { local u=${1#*://}; u=${u%%/*}; [[ "$u" == *:* ]] && echo "${u##*:}" || echo 80; }

run_tests() {
  local name url
  for name in VITE_AUTH_API_BASE_URL VITE_ITEM_API_BASE_URL VITE_MATCHING_API_BASE_URL VITE_ADMIN_API_BASE_URL; do
    [[ -n "${!name:-}" ]] || { echo "$name is not set. Start and source the shared environment first." >&2; return 1; }
  done
  install_jmeter

  local args=(-n -t "$SCRIPT_DIR/plans/lost-and-found-load.jmx" -q "$SCRIPT_DIR/config/load.properties"
    -Jdata_dir="$SCRIPT_DIR/data"
    -Jjmeter.reportgenerator.temp_dir="$ASE_REPORT_DIR/.report-temp"
    -l "$ASE_REPORT_DIR/results.jtl" -j "$ASE_REPORT_DIR/jmeter.log"
    -e -o "$ASE_REPORT_DIR/html")
  local pair service variable
  for pair in auth:VITE_AUTH_API_BASE_URL item:VITE_ITEM_API_BASE_URL matching:VITE_MATCHING_API_BASE_URL admin:VITE_ADMIN_API_BASE_URL; do
    service=${pair%%:*}
    variable=${pair#*:}
    url=${!variable}
    args+=(-J"${service}_host=$(host_of "$url")" -J"${service}_port=$(port_of "$url")")
  done

  local jmeter_result=0 summary_result=0
  echo "Running the load test plan (about one minute)."
  "$JMETER" "${args[@]}" "$@" || jmeter_result=$?
  node "$SCRIPT_DIR/scripts/summarize-results.mjs" "$ASE_REPORT_DIR/results.jtl" \
    "$SCRIPT_DIR/config/thresholds.json" "$ASE_REPORT_DIR" > /dev/null || summary_result=$?
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" && -s "$ASE_REPORT_DIR/summary.md" ]]; then
    cat "$ASE_REPORT_DIR/summary.md" >> "$GITHUB_STEP_SUMMARY"
  fi
  (( jmeter_result == 0 )) || { echo "JMeter exited with code $jmeter_result." >&2; return "$jmeter_result"; }
  return "$summary_result"
}

echo running > "$ASE_REPORT_DIR/status.txt"
trap finish EXIT
exec > >(tee "$ASE_REPORT_DIR/console.log") 2>&1
cd "$ROOT"
run_tests "$@"
