#!/usr/bin/env bash
# Shared application lifecycle and report validation. Tests run in category/run.sh.
set -euo pipefail
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT=$(cd "$SCRIPT_DIR/../.." && pwd)
export ASE_RUNTIME_DIR=${ASE_RUNTIME_DIR:-${TMPDIR:-/tmp}/ase-testing-local}
export ASE_REPORT_ROOT=${ASE_REPORT_ROOT:-${TMPDIR:-/tmp}/ase-testing-reports}
export ASE_COMPOSE_PROJECT=${ASE_COMPOSE_PROJECT:-ase-testing-local}
compose() { docker compose -p "$ASE_COMPOSE_PROJECT" -f "$SCRIPT_DIR/docker-compose.yml" "$@"; }

validate_report() {
  local category=${1:-}
  case "$category" in
    unit-testing|mutation-testing|load-testing|ui-e2e-testing) ;;
    *) echo 'Unknown testing category.' >&2; return 2 ;;
  esac
  local report_dir="$ASE_REPORT_ROOT/$category"
  if [[ ! -s "$report_dir/summary.md" || ! -s "$report_dir/details.json" ]]; then
    echo 'The test run must produce nonempty summary.md and details.json reports.' >&2
    return 1
  fi
  # Validate and display detailed evidence without running any testing commands.
  node - "$report_dir/details.json" <<'JS'
const fs = require('node:fs');
const report = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
if (!Array.isArray(report.cases) || report.cases.length === 0) throw new Error('No cases reported.');
const states = new Set(['passed', 'failed', 'skipped', 'killed', 'survived', 'timeout', 'no-coverage', 'compile-error']);
for (const c of report.cases) {
  if (typeof c.name !== 'string' || !c.name.trim() || typeof c.description !== 'string' || !c.description.trim() || !states.has(c.status)) {
    throw new Error('Every case needs a name, description and valid status.');
  }
  console.log(`[${c.status}] ${c.name}: ${c.description}`);
  if (c.durationMs !== undefined) console.log(`  Duration: ${c.durationMs} ms`);
  if (c.error) console.log(`  Details: ${c.error}`);
}
if (report.cases.some(c => c.status === 'failed')) throw new Error('Failed cases reported by a successful test command.');
if (report.cases.every(c => c.status === 'skipped' || c.status === 'compile-error')) throw new Error('No runnable cases reported.');
JS
}

wait_http() {
  local url=$1 pid=$2
  for ((attempt=1; attempt<=90; attempt++)); do
    if ! kill -0 "$pid" 2>/dev/null; then echo "Application stopped before readiness: $url" >&2; return 1; fi
    if curl --fail --silent --max-time 3 "$url" >/dev/null; then return 0; fi
    sleep 2
  done
  echo "Readiness timed out: $url" >&2
  return 1
}

stop_environment() {
  local result=0 pid
  if [[ -d "$ASE_RUNTIME_DIR/pids" ]]; then
    for file in "$ASE_RUNTIME_DIR"/pids/*.pid; do
      [[ -f "$file" ]] || continue
      pid=$(cat "$file")
      if [[ "$pid" =~ ^[0-9]+$ ]]; then kill "$pid" 2>/dev/null || true; fi
      rm -f -- "$file"
    done
  fi
  if [[ -f "$ASE_RUNTIME_DIR/compose.started" ]]; then
    mkdir -p "$ASE_RUNTIME_DIR/logs"
    compose logs --no-color > "$ASE_RUNTIME_DIR/logs/infrastructure.log" 2>&1 || true
    compose down --timeout 15 || result=$?
    if (( result == 0 )); then rm -f -- "$ASE_RUNTIME_DIR/compose.started"; fi
  fi
  return "$result"
}

start_environment() {
  for command in docker dotnet node npm curl; do
    command -v "$command" >/dev/null || { echo "Missing prerequisite: $command" >&2; return 1; }
  done
  if [[ -f "$ASE_RUNTIME_DIR/compose.started" ]]; then
    echo 'This runtime already owns an environment. Stop it before starting again.' >&2
    return 1
  fi
  # Refuse occupied ports before launching anything or checking another app's readiness.
  node <<'JS'
const net = require('node:net');
Promise.all([3307, 19092, 5261, 5001, 5179, 5003, 5173].map(port => new Promise((resolve, reject) => {
  const server = net.createServer();
  server.once('error', () => reject(new Error(`Required localhost port ${port} is already occupied.`)));
  server.listen(port, '127.0.0.1', () => server.close(resolve));
}))).catch(error => { console.error(error.message); process.exitCode = 1; });
JS
  mkdir -p "$ASE_RUNTIME_DIR/logs" "$ASE_RUNTIME_DIR/pids"
  # Stop only processes/containers started by this lifecycle when startup fails.
  trap 'stop_environment' ERR
  touch "$ASE_RUNTIME_DIR/compose.started"
  compose up -d --wait --wait-timeout 240
  bash "$SCRIPT_DIR/seed-test-data.sh" --databases
  export ASPNETCORE_ENVIRONMENT=Development
  export Jwt__Secret=ase-disposable-test-key-at-least-32-characters
  export Jwt__Issuer=auth-service Jwt__Audience=lostandfound-app
  export Kafka__BootstrapServers=localhost:19092
  export Cors__AllowedOrigins__0=http://localhost:5173
  export ItemService__BaseUrl=http://localhost:5001
  export Auth__FrontendBaseUrl=http://localhost:5173 Frontend__BaseUrl=http://localhost:5173
  export ImageProcessing__Enabled=false Notifications__Enabled=false
  export BlobStorage__ConnectionString='' ApplicationInsights__ConnectionString=''
  # Email workflows need an explicitly configured test SMTP provider (see README).
  export Smtp__Host=${ASE_SMTP_HOST:-localhost} Smtp__Port=${ASE_SMTP_PORT:-2525}
  export Smtp__User=${ASE_SMTP_USER:-} Smtp__Password=${ASE_SMTP_PASSWORD:-}
  export Smtp__FromAddress=${ASE_SMTP_FROM:-assessment@example.test}
  export VITE_AUTH_API_BASE_URL=http://localhost:5261
  export VITE_ITEM_API_BASE_URL=http://localhost:5001
  export VITE_MATCHING_API_BASE_URL=http://localhost:5179
  export VITE_ADMIN_API_BASE_URL=http://localhost:5003
  export ASE_MYSQL_CONNECTION='Server=localhost;Port=3307;Uid=root;Pwd=ase-local-test-password;Allow User Variables=true;'
  # Environment file is private runtime data and is never uploaded as a report.
  (umask 077; export -p | sed -n '/^declare -x \(ASE_\|VITE_\|ASPNETCORE_\|Jwt__\|Kafka__\|Cors__\|ItemService__\|Auth__\|Frontend__\|ImageProcessing__\|Notifications__\|BlobStorage__\|ApplicationInsights__\|Smtp__\)/p' > "$ASE_RUNTIME_DIR/environment.sh")
  local service database port probe pid
  while read -r service database port probe; do
    dotnet build "$ROOT/services/$service/$service.csproj" -c Release > "$ASE_RUNTIME_DIR/logs/$service-build.log" 2>&1
    (
      cd "$ROOT/services/$service"
      export ConnectionStrings__MySql="${ASE_MYSQL_CONNECTION}Database=$database;"
      export Kafka__GroupId="ase-$service"
      export Item__PhotoStoragePath="$ASE_RUNTIME_DIR/photos"
      exec dotnet "bin/Release/net8.0/$service.dll" --urls "http://localhost:$port"
    ) > "$ASE_RUNTIME_DIR/logs/$service.log" 2>&1 &
    pid=$!
    echo "$pid" > "$ASE_RUNTIME_DIR/pids/$service.pid"
    wait_http "http://localhost:$port/$probe" "$pid"
  done <<'SERVICES'
AuthService auth_service 5261 swagger/v1/swagger.json
ItemService item_service 5001 swagger/v1/swagger.json
MatchingService matching_service 5179 health
AdminVerifyService admin_verify_service 5003 health
SERVICES
  (cd "$ROOT/frontend" && npm ci) > "$ASE_RUNTIME_DIR/logs/frontend-install.log" 2>&1
  # Launch Vite directly so the recorded PID owns the server rather than an npm wrapper.
  (cd "$ROOT/frontend" && exec node node_modules/vite/bin/vite.js --host localhost --port 5173 --strictPort) \
    > "$ASE_RUNTIME_DIR/logs/frontend.log" 2>&1 &
  pid=$!
  echo "$pid" > "$ASE_RUNTIME_DIR/pids/frontend.pid"
  wait_http http://localhost:5173 "$pid"
  trap - ERR
  echo "Shared environment ready. Source $ASE_RUNTIME_DIR/environment.sh before running UI/load commands."
}

case "${1:-}" in
  validate-report) validate_report "${2:-}" ;;
  start) start_environment ;;
  stop) stop_environment ;;
  *) echo 'Usage: bash start-environment.sh start|stop|validate-report CATEGORY' >&2; exit 2 ;;
esac
