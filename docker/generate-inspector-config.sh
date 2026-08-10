#!/bin/sh
# Logs into the sample as alice (user) and root (admin) and writes an MCP
# Inspector catalog with three connections to the same endpoint - anonymous,
# alice, and root - so the tool list can be compared per identity.
set -eu

API=${API_URL:-http://todo-api:8080}
OUT=${OUT_FILE:-/shared/mcp-servers.json}

echo "Waiting for $API ..."
until [ "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/auth/login" \
      -H 'Content-Type: application/json' \
      -d '{"username":"alice","password":"password"}')" = "200" ]; do
  sleep 1
done

token() {
  curl -sf -X POST "$API/api/auth/login" \
    -H "Content-Type: application/json" \
    -d "{\"username\":\"$1\",\"password\":\"password\"}" \
    | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p'
}

ALICE=$(token alice)
ROOT=$(token root)
[ -n "$ALICE" ] && [ -n "$ROOT" ] || { echo "Failed to obtain tokens" >&2; exit 1; }

cat > "$OUT" <<EOF
{
  "mcpServers": {
    "nabu-anonymous": {
      "type": "http",
      "url": "$API/mcp"
    },
    "nabu-alice-user": {
      "type": "http",
      "url": "$API/mcp",
      "headers": { "Authorization": "Bearer $ALICE" }
    },
    "nabu-root-admin": {
      "type": "http",
      "url": "$API/mcp",
      "headers": { "Authorization": "Bearer $ROOT" }
    }
  }
}
EOF

echo "Wrote $OUT (anonymous / alice / root)"
