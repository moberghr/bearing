#!/usr/bin/env bash
#
# The SQL Server that Bearing.Data.Tests' SqlServer* suites talk to — the sibling of test-db.sh, which
# does the same for PostgreSQL.
#
# It exists because those suites shipped with no way to run them. They are SkippableFacts (§4.2), so
# without a server they skip cleanly and the build stays green — which meant every claim about the
# sys.* catalog queries, FormatType's byte-vs-char arithmetic, the cumulative RecordsAffected delta,
# KeyInfo column origin and the hoisted-CTE paging was argued rather than observed. One command is the
# difference between "skips" and "proven".
#
#   ./build/test-db-mssql.sh          start it (idempotent)
#   ./build/test-db-mssql.sh status    what is running, and whether it is the default endpoint
#   ./build/test-db-mssql.sh stop      remove the container
#
# Unlike the Postgres one there is no sample schema to load: every SQL Server integration test creates
# its own fixture and drops it in a finally, so an empty database is all they need. That is also why
# this points at a database of its own rather than at master.
set -euo pipefail

NAME="${BEARING_TEST_MSSQL_CONTAINER:-bearing-mssql-test}"
PORT="${BEARING_TEST_MSSQL_PORT:-1433}"
USER_NAME="${BEARING_TEST_MSSQL_USER:-sa}"
PASSWORD="${BEARING_TEST_MSSQL_PASSWORD:-Bearing!Test1}"
DB="${BEARING_TEST_MSSQL_DB:-bearing_test}"
IMAGE="${BEARING_TEST_MSSQL_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"

say() { printf '%s\n' "$*" >&2; }

need_docker() {
  command -v docker >/dev/null 2>&1 || { say "docker is not on PATH."; exit 1; }
  docker info >/dev/null 2>&1 || { say "docker is installed but not running."; exit 1; }
}

# The default port is SQL Server's own, so a developer machine with a local instance is likely to be
# using it already. Talking to that instance by accident would be worse than refusing: these tests
# make and then remove their own fixture tables.
check_port_is_free() {
  if ! (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then
    return 0
  fi
  say ""
  say "  Something is already listening on 127.0.0.1:$PORT, and it is not this container."
  say "  That is the port MsSqlTestServer defaults to, so \`dotnet test\` would talk to it — and these"
  say "  suites make and then remove their own fixture tables. Refusing rather than risking your instance."
  say ""
  say "  If that is a local SQL Server you want to keep, run this one elsewhere:"
  say ""
  say "      BEARING_TEST_MSSQL_PORT=11433 ./build/test-db-mssql.sh"
  say "      BEARING_TEST_MSSQL_PORT=11433 dotnet test tests/Bearing.Data.Tests"
  say ""
  exit 1
}

running() { [ "$(docker inspect -f '{{.State.Running}}' "$NAME" 2>/dev/null || echo false)" = "true" ]; }
exists()  { docker inspect "$NAME" >/dev/null 2>&1; }

# sqlcmd moved between images and releases; 2022 ships it under /opt/mssql-tools18 and wants -C to trust
# the container's self-signed certificate. Fall back to the older path so this keeps working on a 2019 image.
#
# MSYS_NO_PATHCONV=1 is not optional on Windows: under Git Bash, MSYS rewrites anything that looks like a
# Unix path in an argument, so /opt/mssql-tools18/bin/sqlcmd reached docker as
# C:/Program Files/Git/opt/mssql-tools18/bin/sqlcmd and exec failed on a path inside the *host's* Git
# install. This repo is developed on Windows, so the unguarded form is simply broken here.
sqlcmd_in() {
  MSYS_NO_PATHCONV=1 docker exec -i "$NAME" \
      /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U "$USER_NAME" -P "$PASSWORD" "$@" 2>/dev/null \
  || MSYS_NO_PATHCONV=1 docker exec -i "$NAME" \
      /opt/mssql-tools/bin/sqlcmd -S localhost -U "$USER_NAME" -P "$PASSWORD" "$@"
}

start() {
  need_docker

  if running; then
    say "$NAME is already running on port $PORT."
  else
    if exists; then
      say "Starting the existing $NAME…"
      docker start "$NAME" >/dev/null
    else
      check_port_is_free
      say "Creating $NAME from $IMAGE on port $PORT…"
      # MSSQL_PID=Developer is the free edition for non-production use, which is what a test container is.
      docker run -d --name "$NAME" \
        -e ACCEPT_EULA=Y \
        -e MSSQL_SA_PASSWORD="$PASSWORD" \
        -e MSSQL_PID=Developer \
        -p "$PORT:1433" \
        "$IMAGE" >/dev/null
    fi
  fi

  # SQL Server takes appreciably longer than Postgres to accept connections, and the first query after
  # the socket opens can still fail while recovery finishes — so this waits on a real query, not on the
  # port. 240s because a first start also runs the msdb upgrade steps, and measured here that ran past
  # 90s on its own; a container that is genuinely broken still reports inside four minutes.
  say "Waiting for SQL Server to accept a query…"
  local waited=0
  until sqlcmd_in -Q "select 1" >/dev/null 2>&1; do
    waited=$((waited + 2))
    if [ "$waited" -ge 240 ]; then
      say ""
      say "  $NAME did not become ready within 240s. Its log:"
      say ""
      docker logs --tail 30 "$NAME" >&2 || true
      exit 1
    fi
    sleep 2
  done

  # No sample schema on purpose (see the header). Just the database the suites point at.
  sqlcmd_in -Q "if db_id('$DB') is null create database [$DB];" >/dev/null
  say "Ensured database [$DB] exists."

  status
}

status() {
  need_docker
  if ! running; then
    say "$NAME is not running. Start it with: ./build/test-db-mssql.sh"
    exit 1
  fi

  local version
  version="$(sqlcmd_in -h -1 -W -Q "set nocount on; select convert(varchar(20), serverproperty('ProductVersion'))" 2>/dev/null | head -1 | tr -d '\r' || echo '?')"

  say ""
  say "  container  $NAME"
  say "  endpoint   localhost:$PORT/$DB  (user $USER_NAME)"
  say "  sqlserver  $version"
  say ""
  if [ "$PORT" = "1433" ] && [ "$DB" = "bearing_test" ] && [ "$USER_NAME" = "sa" ]; then
    say "  \`dotnet test\` needs no environment variables — these are MsSqlTestServer's defaults."
  else
    say "  This is NOT the default endpoint, so the suites need to be told:"
    say ""
    say "      BEARING_TEST_MSSQL_PORT=$PORT dotnet test"
  fi
}

stop() {
  need_docker
  if exists; then
    say "Removing $NAME…"
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
