#!/usr/bin/env bash
# Shared database bootstrap and optional scenario-data hook. No old QA tests run.
set -euo pipefail
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
ROOT=$(cd "$SCRIPT_DIR/../.." && pwd)
export ASE_COMPOSE_PROJECT=${ASE_COMPOSE_PROJECT:-ase-testing-local}
export ASE_RUNTIME_DIR=${ASE_RUNTIME_DIR:-${TMPDIR:-/tmp}/ase-testing-local}

case "${1:-}" in
  --databases)
    docker compose -p "$ASE_COMPOSE_PROJECT" -f "$SCRIPT_DIR/docker-compose.yml" exec -T mysql \
      sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -u root' <<'SQL'
CREATE DATABASE IF NOT EXISTS auth_service;
CREATE DATABASE IF NOT EXISTS item_service;
CREATE DATABASE IF NOT EXISTS matching_service;
CREATE DATABASE IF NOT EXISTS admin_verify_service;
SQL
    echo 'Assessment databases prepared; applications apply their own migrations on startup.'
    ;;
  load-testing|ui-e2e-testing)
    category=$1
    [[ -f "$ASE_RUNTIME_DIR/environment.sh" ]] || { echo 'Start the shared environment first.' >&2; exit 1; }
    source "$ASE_RUNTIME_DIR/environment.sh"
    hook="$ROOT/testing-pipeline/$category/seed.sh"
    if [[ -f "$hook" ]]; then
      bash "$hook"
    else
      echo "No $category/seed.sh supplied. Only application startup seed data is available."
      echo 'The category executor must create any additional scenario records it needs.'
    fi
    ;;
  *) echo 'Usage: bash seed-test-data.sh --databases|load-testing|ui-e2e-testing' >&2; exit 2 ;;
esac
