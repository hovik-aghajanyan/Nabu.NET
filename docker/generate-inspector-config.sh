#!/bin/sh
# Logs into both samples as alice (user) and root (admin) and writes an MCP
# Inspector catalog with three connections per sample to the same endpoint -
# anonymous, alice, and root - so the tool list can be compared per identity
# on both protocol layers.
set -eu

TODO_API=${TODO_API_URL:-http://todo-api:8080}
BOOKS_API=${BOOKS_API_URL:-http://official-sdk-api:8080}
CHAT_API=${CHAT_API_URL:-http://chat-api:8080}
OUT=${OUT_FILE:-/shared/mcp-servers.json}

# $1: base URL of the API to wait for.
wait_for_login() {
  echo "Waiting for $1 ..."
  until [ "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$1/api/auth/login" \
        -H 'Content-Type: application/json' \
        -d '{"username":"alice","password":"password"}')" = "200" ]; do
    sleep 1
  done
}

# $1: base URL, $2: username. Each sample signs its own tokens, so log in per API.
token() {
  curl -sf -X POST "$1/api/auth/login" \
    -H "Content-Type: application/json" \
    -d "{\"username\":\"$2\",\"password\":\"password\"}" \
    | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p'
}

wait_for_login "$TODO_API"
wait_for_login "$BOOKS_API"
wait_for_login "$CHAT_API"

TODO_ALICE=$(token "$TODO_API" alice)
TODO_ROOT=$(token "$TODO_API" root)
BOOKS_ALICE=$(token "$BOOKS_API" alice)
BOOKS_ROOT=$(token "$BOOKS_API" root)
CHAT_ALICE=$(token "$CHAT_API" alice)
CHAT_ROOT=$(token "$CHAT_API" root)
[ -n "$TODO_ALICE" ] && [ -n "$TODO_ROOT" ] && [ -n "$BOOKS_ALICE" ] && [ -n "$BOOKS_ROOT" ] \
  && [ -n "$CHAT_ALICE" ] && [ -n "$CHAT_ROOT" ] \
  || { echo "Failed to obtain tokens" >&2; exit 1; }

cat > "$OUT" <<EOF
{
  "mcpServers": {
    "todo-anonymous": {
      "type": "http",
      "url": "$TODO_API/mcp"
    },
    "todo-alice-user": {
      "type": "http",
      "url": "$TODO_API/mcp",
      "headers": { "Authorization": "Bearer $TODO_ALICE" }
    },
    "todo-root-admin": {
      "type": "http",
      "url": "$TODO_API/mcp",
      "headers": { "Authorization": "Bearer $TODO_ROOT" }
    },
    "books-anonymous": {
      "type": "http",
      "url": "$BOOKS_API/books/mcp"
    },
    "books-alice-user": {
      "type": "http",
      "url": "$BOOKS_API/books/mcp",
      "headers": { "Authorization": "Bearer $BOOKS_ALICE" }
    },
    "books-root-admin": {
      "type": "http",
      "url": "$BOOKS_API/books/mcp",
      "headers": { "Authorization": "Bearer $BOOKS_ROOT" }
    },
    "chat-anonymous": {
      "type": "http",
      "url": "$CHAT_API/mcp"
    },
    "chat-alice-user": {
      "type": "http",
      "url": "$CHAT_API/mcp",
      "headers": { "Authorization": "Bearer $CHAT_ALICE" }
    },
    "chat-root-admin": {
      "type": "http",
      "url": "$CHAT_API/mcp",
      "headers": { "Authorization": "Bearer $CHAT_ROOT" }
    }
  }
}
EOF

echo "Wrote $OUT (anonymous / alice / root for todo, books and chat)"
