#!/usr/bin/env bash
#
# The DynamoDB Local that Bearing.Data.Tests' DynamoDb* live suites talk to — the sibling of test-db.sh
# (PostgreSQL), test-db-mssql.sh (SQL Server) and test-db-mysql.sh (MariaDB / MySQL).
#
#   ./build/test-db-dynamodb.sh          start it (idempotent)
#   ./build/test-db-dynamodb.sh status    what is running, and whether it is the default endpoint
#   ./build/test-db-dynamodb.sh stop      remove the container
#
# It runs in memory (-inMemory -sharedDb): nothing survives the container, and every test makes its own
# uniquely named tables and drops them in a finally (§4.7), so an empty server is all the suite needs. There
# is no credential to set up either — a loopback endpoint with no AWS profile signs with fixed placeholder
# keys (DynamoDbConnectionFactory), and DynamoDB Local ignores the signature.
#
# It listens on 18000, not on DynamoDB Local's own 8000: that port is a popular one for local web servers,
# and these suites create and delete tables on whatever answers there.
#
# bash 3.2-safe (macOS), like build/velopack.sh.
set -euo pipefail

NAME="${BEARING_TEST_DYNAMODB_CONTAINER:-bearing-dynamodb-test}"
PORT="${BEARING_TEST_DYNAMODB_PORT:-18000}"
IMAGE="${BEARING_TEST_DYNAMODB_IMAGE:-amazon/dynamodb-local:3.1.0}"

say() { printf '%s\n' "$*" >&2; }

need_docker() {
  command -v docker >/dev/null 2>&1 || { say "docker is not on PATH."; exit 1; }
  docker info >/dev/null 2>&1 || { say "docker is installed but not running."; exit 1; }
}

running() { [ "$(docker inspect -f '{{.State.Running}}' "$NAME" 2>/dev/null || echo false)" = "true" ]; }
exists()  { docker inspect "$NAME" >/dev/null 2>&1; }

# True when something accepts a TCP connection on the port.
listening() { (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; }

# DynamoDB Local answers a plain GET with an HTTP response (any status), so a response is proof the server
# is up, not just that docker has bound the port. curl where there is one; the TCP probe is the fallback.
answers() {
  if command -v curl >/dev/null 2>&1; then
    curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$PORT/"
  else
    listening
  fi
}

# The default port is a free one on purpose, but it is still a guess about someone else's machine. Talking
# to whatever happens to be there by accident would be worse than refusing: these tests make and then
# remove their own tables.
check_port_is_free() {
  if ! listening; then
    return 0
  fi
  say ""
  say "  Something is already listening on 127.0.0.1:$PORT, and it is not this container."
  say "  That is the port DynamoDbTestServer defaults to, so \`dotnet test\` would talk to it — and these"
  say "  suites make and then remove their own tables. Refusing rather than risking it."
  say ""
  say "  If you want to keep what is there, run this one elsewhere:"
  say ""
  say "      BEARING_TEST_DYNAMODB_PORT=28000 ./build/test-db-dynamodb.sh"
  say "      BEARING_TEST_DYNAMODB_ENDPOINT=http://localhost:28000 dotnet test tests/Bearing.Data.Tests"
  say ""
  exit 1
}

start() {
  need_docker

  if running; then
    say "$NAME is already running on port $PORT."
  else
    if exists; then
      say "Starting the existing ${NAME}…"
      docker start "$NAME" >/dev/null
    else
      check_port_is_free
      say "Creating $NAME from $IMAGE on port ${PORT}…"
      docker run -d --name "$NAME" \
        -p "127.0.0.1:$PORT:8000" \
        "$IMAGE" \
        -jar DynamoDBLocal.jar -inMemory -sharedDb >/dev/null
    fi
  fi

  say "Waiting for DynamoDB Local to answer…"
  local waited=0
  until answers; do
    waited=$((waited + 1))
    if [ "$waited" -ge 60 ]; then
      say ""
      say "  $NAME did not answer within 60s. Its log:"
      say ""
      docker logs --tail 30 "$NAME" >&2 || true
      exit 1
    fi
    sleep 1
  done

  status
}

status() {
  need_docker
  if ! running; then
    say "$NAME is not running. Start it with: ./build/test-db-dynamodb.sh"
    exit 1
  fi

  say ""
  say "  container  $NAME"
  say "  endpoint   http://localhost:$PORT  (in memory, no credentials)"
  say "  image      $IMAGE"
  say ""
  if [ "$PORT" = "18000" ]; then
    say "  \`dotnet test\` needs no environment variables — this is DynamoDbTestServer's default."
  else
    say "  This is NOT the default endpoint, so the suites need to be told:"
    say ""
    say "      BEARING_TEST_DYNAMODB_ENDPOINT=http://localhost:$PORT dotnet test"
  fi
}

stop() {
  need_docker
  if exists; then
    say "Removing ${NAME}…"
    docker rm -f "$NAME" >/dev/null
  else
    say "$NAME does not exist."
  fi
}

case "${1:-start}" in
  start|"") start ;;
  stop)     stop ;;
  status)   status ;;
  *)        say "usage: $0 [start|stop|status]"; exit 2 ;;
esac
