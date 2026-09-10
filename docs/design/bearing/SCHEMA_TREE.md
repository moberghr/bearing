# Handoff: Bearing — Schema explorer grouping

Companion to `README.md`. Specifies the **schema tree** in the Connections panel — how a database node's
children are arranged. Not built; this is the spec. Two items, from QA on 2026-09-10.

Rules this touches: §9.8 (late reads), §9.9 / §9.9a / §9.9b (what the explorer reads and where each thing
is shown), §1.7 (absences are typed, not blank).

---

## 1. A database node is a flat run of up to 17 groups

### The problem
Expanding a database today produces the relations inline followed by **every** object-kind group as a
sibling:

```
relations… · Schemas · Views · Functions · Procedures · Sequences · Types ·
Extensions · Policies · Publications · Subscriptions · Foreign servers ·
Event triggers · Collations · Casts · Operators · Operator classes · Text search
```

Seventeen groups, none nested. §9.9a placed each one deliberately and each placement is still right in
isolation — the flat run is what the sum of them costs. It is most visible in demo mode, where four
relations are followed by fifteen single-item groups.

The reference for what it should feel like is DBeaver's navigator: a database opens onto a handful of
rows, and the long tail is behind a bucket you only open when you want it.

### The shape: two modes, toggled

**Full mode — schema-first. This is the default.**

```
▾ 🗄 demo                     11 MB · connected
  ▾ 📁 Schemas                          2
    ▾ 📂 public
      ▸ 📁 Tables                       4
      ▸ 📁 Views                        1
      ▸ 📁 Functions                    1
      ▸ 📁 Procedures                   —
      ▸ 📁 Sequences                    2
      ▸ 📁 Types                        3
      ▸ 📁 Policies                     2
      ▸ 📁 Operators                    1
    ▸ 📂 shop
  ▾ 📁 Administer
      ▸ ⬡ Extensions                    2
      ▸ ⚡ Event triggers                1
      ▸ ⇈ Publications                  1
      ▸ ⇊ Subscriptions                 1
      ▸ ⇄ Foreign servers               1
      ▸ aA Collations                   1
      ▸ →  Casts                        1
      ▸ ⌕  Text search                  1
```

**Simple mode — relations inline, one bucket for the rest.**

```
▾ 🗄 demo                     11 MB · connected
  ▸ ▦ document                9.0 MB · ~3 rows
  ▸ ▦ metric                  24 kB · table
  ▸ ▦ payment                 1.6 MB · ~40 rows
  ▸ ▦ store                   80 kB · ~4 rows
  ▸ 👁 Views                              1
  ▸ (f) Functions                         1
  ▸ 📁 Other objects                     14
```

**The toggle sits in the panel header**, beside the existing folder and ＋ buttons — it is a property of
the panel, not of a connection:

```
CONNECTIONS                    [ Simple | Full ]  📁  ＋
┌──────────────────────────────────────────────────────┐
│ Filter name, host or environment…                    │
└──────────────────────────────────────────────────────┘
```

Segmented control, same treatment as the results dock's Stacked / Tabbed toggle (`RESULTS_GRID.md` §2),
and a persisted user preference for the same reason: it is a way of working, not a per-session choice.

### What this reverses, and why that is allowed now
§9.9a says *"A Schemas level is additive, not a restructure. The inline list stays the primary view — it is
what nearly every expand is for."* Full mode makes the schema level primary and moves relations three
clicks deep, which is exactly what that rule refused.

The rule was right **while there was only one view**. Choosing between "one click to a table" and "a
legible hierarchy" had to be settled one way, and one click won. A toggle is what dissolves the choice:
simple mode *is* §9.9a's view, kept intact and reachable in one click. So the constraint to carry forward
is not "relations stay inline" but **"the one-click-to-a-table view must always still exist"**.

### Notes for whoever builds it
- **Simple mode's `Other objects` must not be a lazy shell.** §9.9's kinds are a *late* read (§9.8) that
  appends groups after the tree renders; wrapping them in a bucket means the bucket itself arrives late.
  Either it appears when the read lands, or it is present from the start with a count that fills in — but
  it must never render as an empty group, which reads as "this database has none".
- **Full mode's schema children are already lazy and must stay so** (§9.9a): a schema's children are a
  second set of nodes for relations the snapshot already holds, and a database with two thousand of them
  must not pay for them up front.
- **`Schemas` is currently skipped for a single-schema database.** In full mode it cannot be — it is the
  only route to the relations. A one-schema database should collapse the level instead: show `public`'s
  groups directly under the database.
- **The counts on the group rows are the affordance that makes either mode readable**, and they are what
  tells an empty kind from an unread one. Neither mockup works without them.
- **Switching modes must not re-fetch.** Both shapes are views over the same snapshot plus the same kinds
  read; a toggle that dropped `Children` and reloaded would make a display preference cost a round trip per
  database, and §9.8's generation guard (§9.9b item 1) exists precisely because rebuilding children races
  the late reads.
- **Roles and Tablespaces stay on the server node.** They are cluster-wide (§9.9a); neither mode moves
  them.

---

## 2. A database whose size is unknown renders as blank

### The problem
In demo mode, `demo` reads `11 MB · connected` and `postgres` reads nothing at all. That is the fixture
working as designed — `DemoCatalog.DatabaseSizes()` returns `new DatabaseSize("postgres", null)`, commented
*"A database the demo user cannot connect to: its size is unknown, not zero (#76)"* — and
`SchemaNodes.FillDatabaseSizesAsync` only labels a row when `size.Bytes` is non-null.

But the state is **indistinguishable from three others**: sizes not loaded yet (the read is late and
arrives after the tree renders, #76), the read having failed, and a genuinely sizeless row. It reads as a
defect, which is how it was reported.

### The rule it breaks
§1.7 already settled this shape for roles: *"Absences are typed, not blank: `ConnectionLimit == -1` is
unlimited (never printed as a number) and `ValidUntil == null` is no expiry. Neither is a permission
problem, and rendering either as 'not visible' would assert a cause nobody checked."*

A null size is the same kind of fact and deserves the same treatment. It is specifically **not** an error
and **not** zero.

### The fix
Render the absence, and say only what is known:

```
▸ 🗄 demo            11 MB · connected
▸ 🗄 postgres        size not visible
```

`pg_database_size` is called only where `has_database_privilege(datname, 'CONNECT')` says it will work, so
"not visible" is the honest reading of a null and asserts no cause beyond the one that was checked. A
tooltip can carry the detail ("the connected role has no CONNECT privilege on this database").

Do not use `—` alone: it is as silent as blank. Do not say "0 B" or "unknown size" — the first is a lie and
the second invites the reader to assume a failure.

### Related, and worth fixing in the same pass
`FillDatabaseSizesAsync` swallows every failure:

```csharp
try { sizes = await _browser.GetDatabaseSizesAsync(Connection, CancellationToken.None); }
catch (Exception) { return; }
```

That is right at runtime (a size is best-effort and must not break the tree, §5.2/§5.6) and it makes a
broken query look exactly like a server with nothing to report — §4.7's *"a silently-swallowed read hides
a query bug"*, in a new place. Whatever the swallow keeps from the user, it should not also keep from the
log.
