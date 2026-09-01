using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.Core.Data;

namespace Bearing.Demo;

/// <summary>
/// Whether this process is running as a demo, and what a demo session is made of (#64).
/// <para>
/// Demo mode is <b>process-wide</b>, not a connection you can add beside your real ones. That is the answer
/// to the issue's constraint that the fake provider be "unreachable from any normal connection flow": it is
/// not registered alongside the real one — in a demo session it <i>replaces</i> the registry, and in an
/// ordinary session it is not in the object graph at all. It also removes the failure mode the labelling
/// exists to guard against, since there is no way to have a demo grid and a production grid in one window.
/// </para>
/// <para>
/// It ships in Release rather than being compiled out. Evaluation is the strongest argument for the feature —
/// somebody who has just installed the app and has no Postgres — and a build that excludes it cannot serve
/// that. What keeps it safe is the isolation above plus the ephemeral workspace it runs in
/// (<see cref="DemoWorkspace"/>), not its absence.
/// </para>
/// </summary>
public static class DemoMode
{
    /// <summary>The command-line switch that starts a demo session.</summary>
    public const string Argument = "--demo";

    /// <summary>The environment variable that does the same, for a shortcut or a script.</summary>
    public const string EnvironmentVariable = "BEARING_DEMO";

    /// <summary>
    /// Whether these arguments and this environment ask for a demo session. Both are honoured because they
    /// serve different people: the switch is what a docs author or a bug reporter types, the variable is what
    /// a desktop shortcut carries. Neither is the discoverable route — that is the button on the empty state,
    /// since an environment variable helps nobody who is evaluating the tool.
    /// </summary>
    public static bool Requested(IReadOnlyList<string>? args, Func<string, string?>? readEnvironment = null)
    {
        if (args is not null && args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase)))
            return true;

        var read = readEnvironment ?? Environment.GetEnvironmentVariable;
        return read(EnvironmentVariable) is { Length: > 0 } value
               && !value.Equals("0", StringComparison.Ordinal)
               && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The demo connection's stable id, so a restarted demo restores the same session state.</summary>
    public static Guid ConnectionId { get; } = new("d3305a19-0000-4000-8000-000000000064");

    /// <summary>
    /// The one connection a demo session has.
    /// <para>
    /// Marked <see cref="ConnectionInfo.RequireWriteConfirmation"/> on purpose: §1.2 says the write guard must
    /// not be special-cased for demo mode, and a guarded connection is the more useful demo anyway — the
    /// confirmation is a feature to show, not an obstacle to route around. The environment label and colour
    /// are the existing mechanism (§9.3a), so the tab wash, the env chip and the status-bar rule all say
    /// "demo" without anything new being drawn.
    /// </para>
    /// </summary>
    public static ConnectionInfo Connection { get; } = new()
    {
        Id = ConnectionId,
        Name = "Demo data",
        ProviderId = DemoProvider.ProviderId,
        Host = "demo",
        Port = 0,
        Database = DemoCatalog.Database,
        User = "demo",
        Environment = "demo",
        // Mint, from the environment palette — not a state colour, which the beacon owns (§9.3a).
        EnvironmentColor = "#4FC17E",
        RequireWriteConfirmation = true,
        // The default, deliberately, and it needs no new credential kind: the stored-password path sends no
        // password on its first attempt precisely so a passwordless connection still connects without being
        // interrogated, and the demo provider never opens a socket to authenticate against. Prompting would
        // be theatre; the session's secret store keeps nothing, so the keychain is never reached either
        // (§1.1).
        CredentialKind = CredentialKind.StoredPassword,
    };

    /// <summary>A starter script, so the first thing an evaluator sees is a query they can run and edit.</summary>
    public const string WelcomeScript = """
        -- Bearing demo. No database is involved: these results are fixed, hand-authored data.
        -- Everything else is the real app — try editing a cell, following a foreign key (the ↗ on
        -- store_id), or running the two statements below together.

        select id, store_id, amount, note from shop.payment;

        select id, name, active from shop.store;
        """;
}
