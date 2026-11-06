#!/usr/bin/env bash
#
# The MariaDB and MySQL servers that Bearing.Data.Tests' MySql* suites talk to — the sibling of test-db.sh
# (PostgreSQL) and test-db-mssql.sh (SQL Server). One provider serves both engines, so both are started:
# a suite that only ever ran against one of them would be proving half of what the provider claims.
#
#   ./build/test-db-mysql.sh          start both (idempotent)
#   ./build/test-db-mysql.sh status    what is running
#   ./build/test-db-mysql.sh stop      remove both containers
#
# Neither listens on 3306: a developer machine is likely to have a MySQL there already, and these suites
# create and drop their own fixture tables. Every test makes its own fixture, so an empty database is all
# either server needs.
#
# bash 3.2-safe (macOS), like build/velopack.sh.
set -euo pipefail

PASSWORD="${BEARING_TEST_MYSQL_PASSWORD:-Bearing!Test1}"
DB="${BEARING_TEST_MYSQL_DB:-bearing_test}"

MARIA_NAME="${BEARING_TEST_MARIADB_CONTAINER:-bearing-mariadb-test}"
MARIA_PORT="${BEARING_TEST_MARIADB_PORT:-13306}"
MARIA_IMAGE="${BEARING_TEST_MARIADB_IMAGE:-mariadb:11.4}"

MYSQL_NAME="${BEARING_TEST_MYSQL_CONTAINER:-bearing-mysql-test}"
MYSQL_PORT="${BEARING_TEST_MYSQL_PORT:-13307}"
MYSQL_IMAGE="${BEARING_TEST_MYSQL_IMAGE:-mysql:8.4}"

say() { printf '%s\n' "$*" >&2; }

need_docker() {
  command -v docker >/dev/null 2>&1 || { say "docker is not on PATH."; exit 1; }
  docker info >/dev/null 2>&1 || { say "docker is installed but not running."; exit 1; }
}

running() { [ "$(docker inspect -f '{{.State.Running}}' "$1" 2>/dev/null || echo false)" = "true" ]; }
exists()  { docker inspect "$1" >/dev/null 2>&1; }

# The client binary is `mariadb` in current MariaDB images and `mysql` in MySQL's.
client_in() {
  local name="$1"; shift
  if docker exec "$name" sh -c 'command -v mariadb' >/dev/null 2>&1; then
    docker exec -i "$name" mariadb -uroot -p"$PASSWORD" "$@"
  else
    docker exec -i "$name" mysql -uroot -p"$PASSWORD" "$@"
  fi
}

start_one() {
  local name="$1" port="$2" image="$3" root_env="$4"
  if running "$name"; then
    say "${name} is already running on port $port."
  elif exists "$name"; then
    say "Starting the existing ${name}…"
    docker start "$name" >/dev/null
  else
    if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
      say "Something is already listening on 127.0.0.1:${port} and it is not ${name} — refusing."
      say "Pick another port, e.g. BEARING_TEST_MARIADB_PORT=23306 / BEARING_TEST_MYSQL_PORT=23307."
      exit 1
    fi
    say "Creating ${name} from ${image} on port ${port}…"
    docker run -d --name "$name" \
      -e "$root_env=$PASSWORD" \
      -e MYSQL_DATABASE="$DB" -e MARIADB_DATABASE="$DB" \
      -p "127.0.0.1:$port:3306" \
      "$image" >/dev/null
  fi

  say "Waiting for ${name} to accept a query…"
  local waited=0
  until client_in "$name" -e "select 1" >/dev/null 2>&1; do
    waited=$((waited + 2))
    if [ "$waited" -ge 180 ]; then
      say "${name} did not become ready within 180s. Its log:"
      docker logs --tail 30 "$name" >&2 || true
      exit 1
    fi
    sleep 2
  done
  client_in "$name" -e "create database if not exists \`$DB\`" >/dev/null 2>&1
}

start() {
  need_docker
  start_one "$MARIA_NAME" "$MARIA_PORT" "$MARIA_IMAGE" MARIADB_ROOT_PASSWORD
  start_one "$MYSQL_NAME" "$MYSQL_PORT" "$MYSQL_IMAGE" MYSQL_ROOT_PASSWORD
  status
}

status() {
  need_docker
  local name port
  for pair in "$MARIA_NAME:$MARIA_PORT" "$MYSQL_NAME:$MYSQL_PORT"; do
    name="${pair%%:*}"; port="${pair##*:}"
    if running "$name"; then
      say "  ${name}  localhost:$port/$DB (root)  $(client_in "$name" -N -e 'select version()' 2>/dev/null || echo '?')"
    else
      say "  ${name}  not running — start with ./build/test-db-mysql.sh"
    fi
  done
}

stop() {
  need_docker
  for name in "$MARIA_NAME" "$MYSQL_NAME"; do
    if exists "$name"; then
      say "Removing ${name}…"
      docker rm -f "$name" >/dev/null
    fi
  done
}

case "${1:-start}" in
  start|"") start ;;
  stop)     stop ;;
  status)   status ;;
  *)        say "usage: $0 [start|stop|status]"; exit 2 ;;
esac
