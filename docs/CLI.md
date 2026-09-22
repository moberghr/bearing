# `bearing` — querying Bearing's connections from outside

`bearing` is one command for both halves of Bearing. On its own it opens the window:

```console
$ bearing          # opens the Bearing app
```

Given a command, it runs read-only queries against the connections the user has **exposed**. That half is
meant for AI agents and scripts: an agent can explore and query a database without ever being told the host,
the user, the database name or the password, and without being able to write.

It does not talk to a running Bearing and does not need one. It reads the same `project.json` and the same OS
keychain the app does, and runs statements through the same code the editor runs them through.

## The two gates

Nothing is reachable unless **both** are true:

1. The connection's owner ticked **"Let the `bearing` command query this connection, read-only"** in the
   connection dialog's Safety group. The default is off, for every connection that exists and every one
   that ever existed.
2. The credential can be obtained without a window — a password in the OS keychain, an OS identity, or an
   `az` session. A connection that asks for its password each session can only be opened in Bearing itself,
   and is listed as unavailable with that reason.

**The boundary is "anything running as you."** `bearing` runs as the user and can read the same keychain
the app reads; what stops it querying a server is that nobody marked the connection. That is a real gate, and
it is the only one. It is not a sandbox, and nothing here should be described as one.

**And it gates discovery, not access.** The mark lives in `project.json` and the keychain is keyed by the
connection's id, so anything running as you can copy that file, set the mark on connections you never
exposed, and point `--project` at the copy. Read-only is still forced — the settings are applied when the
connection is opened, not read from the file — so what this widens is *which servers* a caller can read, not
what it can do to them. The mark is the right place for the decision and it is worth making; it is not a
thing that holds against the account it runs under. The control that holds is the database role.

## Commands

```
bearing                                Open the Bearing window.
bearing [--project <dir>] [--table] <command> [arguments]

  connections                        The exposed connections: name, engine, environment.
  tables <connection>                Tables and views. --schema <name> narrows it.
  describe <connection> <table>      Columns, types, nullability, primary key, foreign keys.
  query <connection> [sql]           Run a read-only query. --file <path> to read it from a file
                                     (- for stdin), --out <path> to write .csv or .xlsx.
  explain <connection> [sql]         Its query plan, as a tree. --analyze to measure it.
                                     PostgreSQL only; other engines report plans in a form this
                                     cannot read, and say so rather than sending the statement.
```

```console
$ bearing query reporting --file monthly.sql --out report.xlsx
Wrote 12,480 rows in 3 result sets to /home/k/report.xlsx (xlsx).

$ echo "select count(*) from film" | bearing query reporting --file -
```

An xlsx takes **one sheet per result set**; a CSV holds one table and refuses a batch that returned more,
because the caller named a path and a script that finds no file at it is broken in a way an error is not.
With `--out` the row count is unlimited unless you pass `--max-rows`, since a capped export is a silently
truncated file. Everywhere else the default is 200 rows — and an explicit `--max-rows` is honoured at any
size rather than clamped. If a cap did stop the read, the response says so (`"truncated": true`) — the file
itself cannot.

**One statement written to a CSV streams**: rows reach the file a batch at a time and the whole result is
never in memory, so `--out report.csv` can be pointed at a table larger than this process. It is written to
a temp file and moved into place at the end, so a read that fails part-way — a timeout, a dropped
connection, a server error at row 900,000 — leaves no file rather than a convincing fraction of one.

**An xlsx cannot stream.** A workbook has to be built from every row, so an `.xlsx` export is bounded by
memory however it was asked for. So is a CSV of a multi-statement batch, which would not be one table
anyway. For a very large result, ask for one statement and a `.csv`.

`--timeout <secs>` can only **lower** what the connection already allows. Raising it would let a caller
lift a limit its owner set, which is the inversion §1.9 exists to prevent.

Output is JSON by default — that is what a script or an agent should parse, and it is stable. `--table` is
for a person reading a terminal and is explicitly *not* stable; it also renders a null and an empty string
identically, which only the JSON tells apart.

Exit codes: `0` fine, `1` the command could not be completed (the reason is on stderr), `2` the arguments
were wrong. A refusal never writes to stdout, so a caller reading stdout gets data or nothing.

```console
$ bearing connections --table
name         engine      access     available
-----------  ----------  ---------  ---------
agent-reads  PostgreSQL  read-only  true

$ bearing query agent-reads "select title, rental_rate from film order by title limit 3" --table
title             rental_rate
----------------  -----------
ACADEMY DINOSAUR  0.99
ACE GOLDFINGER    4.99
ADAPTATION HOLES  2.99
(3 rows in 758 ms)

$ bearing query agent-reads "update film set title = 'x'"
'agent-reads' is exposed to external tools for reads only, so UPDATE will not run. Its owner can widen
that in Bearing, or run the statement there themselves.
$ echo $?
1
```

## What stops a query doing damage

Three things, in the order they apply. The first is the one that actually holds.

**1. Only reads are sent at all.** An exposed connection accepts a statement only if its leading keyword is
one this engine calls a read — on PostgreSQL `SELECT`, `WITH`, `EXPLAIN`, `SHOW`, `TABLE`, `VALUES`.
Everything else is refused before it reaches the server, including `SET`, `BEGIN`, `COMMIT` and any shape
nobody listed. That last part is the point: a new SQL construct defaults to refused.

```console
$ bearing query agent-reads "begin read write; select nextval('film_film_id_seq')"
agent-reads: 'BEGIN' is not allowed on a connection exposed to external tools — it is what would let a
read-only session become writable. Send one read.
```

That example is not hypothetical. Before the allow-list existed, `begin read write; select nextval(…)` and
`set transaction read write; select nextval(…)` both **ran**, on a connection exposed read-only — because
neither `BEGIN` nor `SET` is a write, and the read-only session setting governs the *next* transaction
rather than the one already in progress.

**2. The session is opened read-only anyway.** On PostgreSQL that rides the startup packet, so the server
refuses writes the client could not see — a volatile function, dynamic SQL:

```console
$ bearing query agent-reads "select nextval('film_film_id_seq')"
agent-reads: cannot execute nextval() in a read-only transaction
```

That message is Postgres', not Bearing's. On **SQL Server** there is no session read-only to ask for, so
layer 1 is the whole of it there.

**2b. Three catalogs are not readable**, because of what they hold rather than what they are:
`pg_authid` and `pg_shadow` (password hashes), `pg_subscription` (a publisher's connection string) and
`pg_user_mappings` (a foreign server's password, shown in full to the mapping's owner — so this one does not
even need a privileged connection). Bearing's own catalog reads have never selected these columns; an
exposed connection could, until it couldn't.

**3. A handful of functions are refused inside otherwise-ordinary reads** — `pg_terminate_backend`,
`pg_cancel_backend`, `pg_read_file`, `pg_ls_dir`, `set_config`, `lo_import`/`lo_export`. These are reads, or
signals, so read-only never bounded them; measured on a superuser connection with read-only fully in force,
`select pg_read_file('/etc/passwd')` returned the file.

**Layers 2b and 3 stop accidents, not a determined caller**, and it would be dishonest to present them
otherwise: a name can be reached through a wrapper function, a `search_path` that resolves elsewhere, or SQL
built at runtime. They are read off the statement's *tokens*, so a comment between the name and its
parenthesis no longer hides a call — that one was a text match and four characters defeated it.

### The boundary is the database role

Layers 1–3 are a very good fence. The thing that is a *wall* is the role the connection authenticates as.
If you expose a connection, point it at a role that cannot do the damage in the first place:

```sql
create role agent_ro nosuperuser nologin;
grant connect on database app to agent_ro;
grant usage on schema public to agent_ro;
grant select on all tables in schema public to agent_ro;
alter role agent_ro connection limit 5;
```

That last line matters for a different reason: one `bearing` invocation opens exactly one connection
(measured), but nothing bounds how many invocations run at once, and no client-side setting can. A
`CONNECTION LIMIT` can.

If the connection is a **superuser**, none of layers 1–3 is worth much on its own — a superuser `SELECT`
reads the server's filesystem and `pg_authid`. That is not a reason to refuse superuser connections; it is
the reason the role is the control that matters.

### Two more limits on every exposed session

A statement timeout is filled in (30 s) when the connection has none, so a runaway query cannot outlive the
caller — it is per statement, so a long script can still take a long time. And the connection's
manual-commit setting is cleared, because nothing here can press Commit.

## Which project

`--project <dir>`, or the most recently opened project when it is omitted. Put the explicit form in anything
you save: the fallback follows whoever last opened a project in Bearing.

The project file is re-read on every invocation, so revoking a connection's exposure takes effect on the next
command — there is no cached grant to wait out.

## What it records

Every `query` writes a row to the same SQLite query log the app keeps, marked with its origin — so
`bearing`'s statements sit in your history beside your own and are told apart at a glance:

```
12:04  [cli] select count(*) from payment      88 ms
12:04        select * from film limit 100      12 ms
```

**A refused statement is recorded too**, with the refusal as its error. Strictly nothing ran, and §1.3 calls
the log the record of SQL the user ran — but the reason this path exists is that the caller is not the
person at the keyboard, and "an agent tried to lift read-only and was stopped" is the most useful line the
log can hold.

The log is the user's own settings: retention and literal redaction are honoured exactly as the app applies
them, because §1.3 makes redaction a property of *when a row was written*. The audit export (#113) carries
the origin, so "what ran against production last week" includes what an agent ran and says so.

`tables` and `describe` are not logged — they read the catalog, and §9.13's precedent is that a panel's own
reads are not the record of what someone ran.

## A cost worth knowing

Every invocation is a fresh process and therefore a fresh connection: there is no pool to reuse, and `tables`
and `describe` re-read the catalog each time. That is the right trade for a command called a few times a
minute and the wrong one inside a loop — prefer one well-aimed `query` with an aggregate over many small ones.

## Installing it

`bearing` ships inside Bearing's own package, so it installs, updates and uninstalls with the app and can
never drift from the project format it reads.

- **macOS** — the Homebrew cask puts it on `PATH`; `brew install --no-quarantine moberghr/bearing/bearing`.
- **Windows** — the installer puts the install directory on your user `PATH`, and takes it off again when
  you uninstall. Every update re-asserts the entry and adding is idempotent, so a reinstall does not leave
  two. It is read and written unexpanded, so a `%SystemRoot%` already in your `PATH` stays a variable
  rather than being frozen to today's value.
- **Linux** — beside the app in the install directory; where that goes on `PATH` is the packager's call.

Inside the install directory there are two executables: `bearing` (this command) and `bearing-app` (the
window). It is round that way because a command has to be a console program — a Windows GUI executable
cannot write to a pipe or return a useful exit code — and the name people already know should be the one
they can type. The Start Menu entry and the macOS bundle point at `bearing-app`, and `bearing` with no
arguments starts exactly that, so nothing about opening Bearing changes.

## Registering it with an agent

Claude Code needs no configuration — it has a shell. Allowing it without a prompt each time is one permission
rule:

```
Bash(bearing query:*)
Bash(bearing connections:*)
Bash(bearing tables:*)
Bash(bearing describe:*)
```

Telling the agent what it has is worth a line in `CLAUDE.md`:

> Databases are reachable read-only through `bearing`. Run `bearing connections` to see which, then
> `bearing tables <name>` and `bearing query <name> "<sql>"`. Writes are refused.

An MCP server was built for this and dropped in favour of the CLI: it reached clients with no shell, but cost
tool definitions in every session's context and could not be scripted. If it is wanted again it is another
front end over the same internals, not a second implementation.
