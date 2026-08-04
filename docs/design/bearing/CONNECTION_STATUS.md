# Handoff: Bearing — Connection status & connect/disconnect

Companion to `README.md`. Specifies the **connection state indicator** and the **connect / cancel / disconnect** control in the toolbar, plus its status-bar mirror. Same target (**.NET / Avalonia, MVVM**) and tokens as the parent handoff. Reference prototype: `Bearing - Editor.dc.html` (design reference, not production code).

---

## 1. Two signals, one row

| Thing | Color source | Answers |
|---|---|---|
| **Status dot + label** | Live state | *Is the session up?* |
| **Database chip dot** (right of the db name) | The database's assigned **environment color** (rose = production, gold = staging, mint = local) | *Which environment am I pointed at?* |

**Bearing rule (changed from the previous design):** when **Connected**, the status dot and label take the **environment color** — a live production session reads rose, staging gold, local mint. Connecting is always gold, Disconnected always grey. The rationale: the only time the environment matters is when the session is live, so the "we're connected" affirmation and the "this is production" warning become the same signal instead of competing green/red dots. The status-bar 3px top border stays the environment color unconditionally.

---

## 2. States

`ConnectionState`: `Connected | Connecting | Disconnected`.

| State | Dot | Glow | Animation | Label | Label/icon color |
|---|---|---|---|---|---|
| `Connected` | environment color | `0 0 6px <env>` | none | `Connected` | environment color |
| `Connecting` | `#E3B457` | none | pulse opacity 1 → .3 → 1, 1s, infinite | `Connecting…` | `#E3B457` |
| `Disconnected` | `#4E5865` | none | none | `Disconnected` | `#79838F` |

Dot 8px, `flex:none`. Label 11px / 600, no wrap, 6px gap; the label text is also the group tooltip. Reserve a fixed min-width for the dot+label group (widest string is `Disconnected`) so the adjacent button doesn't shift.

---

## 3. Toolbar control (order, left → right)

`[ PROJ ▾ ] │ [ 🖧 host ▾ ]  ● Connected  [⛓]  ›  [ 🗄 db ● ▾ ]  [ ▶ ]`

- **Status group** — dot + label, non-interactive, tooltip = the label.
- **Toggle button** — 30×30, transparent fill, 1px `#333C48`, radius 7; icon color = current state color.
  - `Connected` / `Connecting`: **linked** chain glyph (unbroken bar between the links).
  - `Disconnected`: **broken** chain glyph (gap in the middle bar).
  - Tooltip: `Connect to server` / `Cancel connecting` / `Disconnect from server`.
- Click:
  - `Disconnected` → **Connect**: → `Connecting`, then `Connected` (prototype fakes ~900 ms; real impl resolves on the driver's open result).
  - `Connecting` → **Cancel**: abort the pending open → `Disconnected`.
  - `Connected` → **Disconnect**: close the session → `Disconnected`.

Guard: the async connect must be cancellable and must never flip to `Connected` after a cancel — use a `CancellationToken` per attempt and ignore results from stale attempts.

---

## 4. Status bar mirror

The bottom bar repeats the same dot, then the **connection name** (env label) in the state color, the state label, `· <db> @ <host> · 30 tables`, and right-aligned `Ln 4, Col 24 · UTF-8 · PostgreSQL 16.2`. The bar's **3px top border** is the environment color and is independent of connection state.

---

## 5. Downstream effects (recommendations)
- **Disconnected:** Run (▶) and result actions disabled. Schema tree and results keep their last-loaded content but read as stale — do not clear them. Opening a script or hitting Run offers one-click reconnect rather than an error.
- **Connecting:** Run disabled; the toggle acts as Cancel.

---

## 6. ViewModel sketch

```csharp
public enum ConnectionState { Disconnected, Connecting, Connected }

ConnectionState State { get; }
IBrush EnvironmentBrush { get; }             // rose / gold / mint — also db chip dot + status-bar top border
IBrush StatusColor => State switch {
    ConnectionState.Connected  => EnvironmentBrush,   // env color when live
    ConnectionState.Connecting => Gold,               // #E3B457
    _                          => Steel500 };         // #79838F (dot #4E5865)
string StatusLabel => State switch { Connected  => "Connected",
                                     Connecting => "Connecting…",
                                     _          => "Disconnected" };
bool   IsLinked   => State != ConnectionState.Disconnected;   // chain vs broken-chain icon
string ToggleTip  => State switch { Disconnected => "Connect to server",
                                    Connecting   => "Cancel connecting",
                                    _            => "Disconnect from server" };
ICommand ToggleConnectionCommand { get; }
```

Bind glow and pulse via style triggers on `State`; don't compute them per render.

---

## 7. Files
- `Bearing - Editor.dc.html` — prototype. Click the chain button in the toolbar to cycle Connected → Disconnected → Connecting → Connected and watch both indicators; switch `connectionEnv` in Tweaks to see the connected state recolor.
