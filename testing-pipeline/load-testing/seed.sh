#!/usr/bin/env bash
# Load-testing scenario data: a few listings so search and view have results from the first request.
# Called by shared/seed-test-data.sh load-testing after the environment is started and sourced.
set -euo pipefail
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
AUTH_URL=${VITE_AUTH_API_BASE_URL:-http://localhost:5261}
ITEM_URL=${VITE_ITEM_API_BASE_URL:-http://localhost:5001}
SEED_USERS=${LOAD_SEED_USERS:-6}
RUN_ID=$(date -u +%Y%m%d%H%M%S)
DATE=$(date -u -d yesterday +%F)

# Each seeded user reports one lost and one found item (2 per user stays under the spam rule's 3).
LOST=("Black leather wallet|Accessories" "Silver phone with blue case|Electronics" "Grey laptop backpack|Bags")
FOUND=("Set of house keys on red ring|Keys" "Navy rain jacket|Clothing" "Passport in a brown cover|Documents")

json_field() { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const v=JSON.parse(s)[process.argv[1]];if(!v)process.exit(1);console.log(v)})' "$1"; }

login() {
  curl --fail --silent --show-error -X POST "$AUTH_URL/api/auth/login" \
    -H 'Content-Type: application/json' \
    -d "{\"email\":\"$1\",\"password\":\"$2\"}" | json_field token
}

create() {
  local token=$1 type=$2 title=$3 category=$4 dateField locationField
  if [[ "$type" == lost ]]; then dateField=DateLost; locationField=LastKnownLocation; else dateField=DateFound; locationField=LocationFound; fi
  curl --fail --silent --show-error -o /dev/null -X POST "$ITEM_URL/api/items/$type" \
    -H "Authorization: Bearer $token" \
    -F "Title=$title (seed $RUN_ID)" -F "Category=$category" \
    -F "Description=Load-test seed listing: $title." \
    -F "$dateField=$DATE" -F "$locationField=Main campus library" \
    -F "HiddenInformation=Seed detail $RUN_ID"
}

created=0
tail -n +2 "$SCRIPT_DIR/data/users.csv" | head -n "$SEED_USERS" | while IFS=, read -r email password; do
  index=$created
  token=$(login "$email" "$password")
  IFS='|' read -r title category <<< "${LOST[index % ${#LOST[@]}]}"
  create "$token" lost "$title" "$category"
  IFS='|' read -r title category <<< "${FOUND[index % ${#FOUND[@]}]}"
  create "$token" found "$title" "$category"
  created=$((created + 1))
  echo "Seeded 2 listings for $email."
done

echo "Load-testing seed complete: $SEED_USERS users, $((SEED_USERS * 2)) listings (run $RUN_ID)."
