#!/usr/bin/env bash
# Provision the PostgreSQL the integration suites expect.
#
# CLAUDE.md and tests/Shared/PgTestServer.cs have named this container and port since the suites were
# written, and nothing created it — so 43 of the 94 data-layer tests skipped on every machine that had not
# built it by hand. Those tests cover the catalog reads behind the schema tree (#46), object sizes (#76),
# live TLS behaviour (#23) and the Npgsql temporal mappings the timestamp display depends on (#77): all of
# them decoration on a default `dotnet test` until this exists.
#
#   ./build/test-db.sh          start it (idempotent), load pagila, verify
#   ./build/test-db.sh stop     stop and remove the container
#   ./build/test-db.sh status    say whether it is up and what is in it
#
# The container name, port, user and password match PgTestServer's defaults exactly, so `dotnet test` needs
# no environment variables once this has run. Override any of them with BEARING_TEST_PG_* as usual.
set -euo pipefail

NAME="${BEARING_TEST_PG_CONTAINER:-squirrel-pg-test}"
PORT="${BEARING_TEST_PG_PORT:-55434}"
USER_NAME="${BEARING_TEST_PG_USER:-postgres}"
PASSWORD="${BEARING_TEST_PG_PASSWORD:-squirrel}"
DB="${BEARING_TEST_PG_DB:-pagila}"
# 18, not 17: pagila's current schema uses uuidv7(), which PostgreSQL added in 18. It is also the version
# that stores every NOT NULL as a pg_constraint row (contype 'n'), which the schema tree's constraint read
# filters out — so provisioning 18 exercises the newer catalog behaviour rather than only the older one.
IMAGE="${BEARING_TEST_PG_IMAGE:-postgres:18-alpine}"

# Pagila is the sample database the existing suites query by name (film, rental, get_customer_balance).
#
# Pinned to a tag rather than master, and deliberately: master's schema now needs uuidv7() (PostgreSQL 18)
# and the pgvector extension, neither of which the stock postgres image has — so a `master` load fails
# halfway and leaves a half-built database, which is worse than not starting. v3.1.0 needs no extensions and
# carries every table and function the suites name.
PAGILA_REF="${BEARING_TEST_PG_PAGILA_REF:-pagila-v3.1.0}"
PAGILA_BASE="https://raw.githubusercontent.com/devrimgunduz/pagila/$PAGILA_REF"

say() { printf '%s\n' "$*" >&2; }

need_docker() {
  command -v docker >/dev/null 2>&1 || { say "docker is not on PATH."; exit 1; }
  docker info >/dev/null 2>&1 || { say "docker is installed but not running."; exit 1; }
}

# Refuse to fight whatever already holds the port, and say what it is.
#
# Not paranoia, and the reason the default port is 55434 rather than 5434: on the machine this script was
# written on, 5434 was an AWS Session Manager tunnel, so PgTestServer's documented defaults were reaching a
# *real remote database* and being turned away by its pg_hba.conf. The suites skipped, which looked like "no
# server" and was actually "a server that refused us" — and several of those tests create and drop schemas.
# Moving the default off 5434 is the cheap half of the fix; this check is what catches the next collision.
check_port_is_free() {
  # bash's own /dev/tcp, so the check does not depend on an interpreter being installed. It used to be a
  # `python -c` guarded by `command -v python` with no else — and bash's `if` with a false condition and no
  # else returns 0, so on any image shipping only `python3` the function returned success, printed nothing,
  # and the safety feature this script is justified by was silently absent. The connect runs in a subshell so
  # the descriptor it opens is closed for us whether it succeeded or not.
  if ! (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then
    return 0
  fi

  say ""
  say "  Something is already listening on 127.0.0.1:$PORT, and it is not this container."
  say "  That is the port PgTestServer defaults to, so \`dotnet test\` would talk to it."
  say ""
  say "  Find it with:   netstat -ano | grep $PORT     (then look the PID up)"
  say "  Or run this DB elsewhere:"
  say ""
  say "      BEARING_TEST_PG_PORT=55435 ./build/test-db.sh"
  say "      BEARING_TEST_PG_PORT=55435 dotnet test"
  say ""
  exit 1
}

running() { [ "$(docker inspect -f '{{.State.Running}}' "$NAME" 2>/dev/null || echo false)" = "true" ]; }
exists()  { docker inspect "$NAME" >/dev/null 2>&1; }

psql_in() { docker exec -i -e PGPASSWORD="$PASSWORD" "$NAME" psql -U "$USER_NAME" "$@"; }

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
      docker run -d \
        --name "$NAME" \
        -e POSTGRES_USER="$USER_NAME" \
        -e POSTGRES_PASSWORD="$PASSWORD" \
        -e POSTGRES_DB="$DB" \
        -p "127.0.0.1:$PORT:5432" \
        "$IMAGE" >/dev/null
    fi
  fi

  # Bound wait: the tests skip rather than fail on an unreachable server, so a hang here would be the worst
  # of both — a script that neither works nor gives up.
  say "Waiting for it to accept connections…"
  for _ in $(seq 1 60); do
    if docker exec "$NAME" pg_isready -U "$USER_NAME" -q >/dev/null 2>&1; then
      say "Ready."
      break
    fi
    sleep 1
  done
  docker exec "$NAME" pg_isready -U "$USER_NAME" -q >/dev/null 2>&1 || {
    say "It never became ready. \`docker logs $NAME\` will say why."
    exit 1
  }

  load_pagila
  stamp
  status
}

# Mark the database as a sanctioned test target.
#
# The tests that CREATE and DROP schemas check for this before doing so. The reason is concrete: on the
# machine this was written on, PgTestServer's default port was an AWS Session Manager tunnel to a real remote
# database, and the suites were reaching it and being turned away by its pg_hba.conf. They skipped, which
# read as "no server" and was actually "a server that refused us". Had the credentials matched, DDL would
# have run there. A stamp the provisioning script owns is the difference between "reachable" and "ours".
stamp() {
  psql_in -d "$DB" -q <<'SQL'
create table if not exists public.bearing_test_marker (
  stamped_at timestamptz not null default now(),
  note text not null
);
insert into public.bearing_test_marker (note)
select 'provisioned by build/test-db.sh — safe to create and drop objects in'
where not exists (select 1 from public.bearing_test_marker);
SQL
  say "Stamped as a Bearing test database."
}

load_pagila() {
  # Idempotent: the schema load is skipped when film is already there, so re-running the script is cheap and
  # does not duplicate the data.
  if psql_in -d "$DB" -Atc "select 1 from information_schema.tables where table_name = 'film'" 2>/dev/null | grep -q 1; then
    say "pagila is already loaded."
    return
  fi

  command -v curl >/dev/null 2>&1 || { say "curl is not on PATH — cannot fetch pagila."; exit 1; }

  # Cleaned up explicitly rather than with a RETURN trap: bash inherits that trap into the *next* function
  # to return, where $tmp no longer exists, and `set -u` then aborts the script after it has already
  # succeeded — which is how this printed "tmp: unbound variable" under a working database.
  local tmp
  tmp="$(mktemp -d)"

  # Cleanup is a function rather than a line at the end, because the end is only the success path: the curl
  # failure returns and the schema failure exits, and both used to leave the download behind — on exactly the
  # paths a flaky network hits over and over.
  cleanup_tmp() { [ -n "${tmp:-}" ] && rm -rf "$tmp"; }

  say "Fetching pagila…"
  for part in pagila-schema.sql pagila-data.sql; do
    curl -fsSL "$PAGILA_BASE/$part" -o "$tmp/$part" || {
      say "Could not download $part. The suites will still run the tests that build their own schema;"
      say "the ones that query pagila by name will skip."
      cleanup_tmp
      return
    }
  done

  say "Loading the schema…"
  if ! psql_in -d "$DB" -q -v ON_ERROR_STOP=1 < "$tmp/pagila-schema.sql"; then
    say ""
    say "  The schema load failed, so the database is half-built — which would make the suites fail rather"
    say "  than skip, and for a reason that has nothing to do with the code. Removing it."
    docker rm -f "$NAME" >/dev/null 2>&1 || true
    cleanup_tmp
    exit 1
  fi
  say "Loading the data (this is the slow part)…"
  psql_in -d "$DB" -q -v ON_ERROR_STOP=1 < "$tmp/pagila-data.sql"

  # The size and row-count reads (#76) report -1 for a never-analysed table, which is correct but means a
  # freshly loaded database exercises only the unknown branch.
  say "Analysing, so table sizes and row estimates have something to report…"
  psql_in -d "$DB" -q -c "analyze"

  cleanup_tmp
}

status() {
  need_docker
  if ! running; then
    say "$NAME is not running. Start it with: ./build/test-db.sh"
    return 1
  fi

  local version tables
  version="$(psql_in -d "$DB" -Atc 'show server_version' 2>/dev/null || echo '?')"
  tables="$(psql_in -d "$DB" -Atc "select count(*) from information_schema.tables where table_schema = 'public'" 2>/dev/null || echo '?')"

  say ""
  say "  container  $NAME"
  say "  endpoint   localhost:$PORT/$DB  (user $USER_NAME)"
  say "  postgres   $version"
  say "  tables     $tables in public"
  say ""
  if [ "$PORT" = "55434" ] && [ "$DB" = "pagila" ] && [ "$USER_NAME" = "postgres" ]; then
    say "  \`dotnet test\` needs no environment variables — these are PgTestServer's defaults."
  else
    say "  This is NOT the default endpoint, so the suites need to be told:"
    say ""
    say "      BEARING_TEST_PG_PORT=$PORT dotnet test"
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
