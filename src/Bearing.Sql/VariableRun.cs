using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Bearing.Core.Data;
using Bearing.Core.Logging;

namespace Bearing.Sql;

/// <summary>Where a variable's value in a run comes from.</summary>
public enum VariableSource
{
    /// <summary>Nothing sets it before its use — the user is asked.</summary>
    Prompt,
    /// <summary>A <c>@set</c> line above the run, in the part of the buffer that isn't running.</summary>
    Above,
    /// <summary>A <c>@set</c> line inside the run's own text, above the references it governs.</summary>
    Script,
}

/// <summary>
/// One value a run binds: a variable, as governed by one <c>@set</c> line (or by none). A variable set twice in a
/// run has two slots — the references under each line get that line's value — so one name can carry two values
/// in one run, each under its own placeholder.
/// </summary>
/// <param name="Name">The variable as first written in the references this slot covers.</param>
/// <param name="Placeholder">The driver-side name: <c>bearing_id</c>, then <c>bearing_id__2</c> for a second slot.</param>
/// <param name="Source">Where the value comes from.</param>
/// <param name="Value">The <c>@set</c> value; null for <see cref="VariableSource.Prompt"/> (and for SQL NULL).</param>
/// <param name="Kind">The <c>@set</c> value's kind.</param>
/// <param name="Line">The 1-based buffer line of the governing <c>@set</c>, or null for a prompt.</param>
/// <param name="Quoted">Written <c>:'name'</c> at least once: binds as text whatever it looks like.</param>
public sealed record VariableSlot(
    string Name, string Placeholder, VariableSource Source, string? Value, QueryParameterKind Kind, int? Line, bool Quoted)
{
    public bool IsPreset => Source != VariableSource.Prompt;
}

/// <summary>
/// The first half of running a script with <c>:name</c> variables (#103): which value governs each reference,
/// and which are set already. Pure; <see cref="VariableRun.Bind"/> is the second half.
/// </summary>
/// <param name="Text">The run as written, <c>@set</c> lines and all.</param>
/// <param name="Sql">The run with its <c>@set</c> lines blanked out (offsets unchanged).</param>
/// <param name="Slots">One per value bound, in order of first use.</param>
/// <param name="Uses">Each reference and the slot that governs it, in text order.</param>
/// <param name="Directives">The run's own <c>@set</c> lines, each with the slot it governs (null when nothing below
/// it uses it).</param>
public sealed record VariablePlan(
    string Text,
    string Sql,
    IReadOnlyList<VariableSlot> Slots,
    IReadOnlyList<(SqlVariableRef Ref, int Slot)> Uses,
    IReadOnlyList<(ScriptDirective Directive, int? Slot)> Directives)
{
    /// <summary>Whether any value still has to be asked for.</summary>
    public bool HasMissing => Slots.Any(s => !s.IsPreset);
}

/// <summary>A run ready to send: the SQL with driver placeholders and their values, and what the log keeps.</summary>
/// <param name="Sql">What reaches the server: <c>:name</c> rewritten to driver placeholders, <c>@set</c> lines
/// blanked. Paging, counting and fetch-all re-run this with the same <see cref="Parameters"/>.</param>
/// <param name="Parameters">The bound values, one per slot.</param>
/// <param name="LogSql">What the user wrote, placeholders kept: its own <c>@set</c> lines stay, each showing the
/// value it actually gave (an override included), so reopening it runs the same thing.</param>
/// <param name="LogParameters">The values from outside the run's text — set above it, or typed into the
/// prompt — which history puts back as <c>@set</c> lines above <see cref="LogSql"/>.</param>
public sealed record PreparedRun(
    string Sql,
    IReadOnlyList<QueryParameter> Parameters,
    string LogSql,
    IReadOnlyList<QueryLogParameter> LogParameters);

public static class VariableRun
{
    /// <summary>
    /// Decide which value governs each reference in <paramref name="sql"/>. A <c>@set</c> applies to what is
    /// <em>below</em> it: inside the run, the nearest line above a reference wins; with none there, the last
    /// one in <paramref name="preamble"/> (the buffer above the run) does; with none at all the value is asked
    /// for. So <c>select :id; @set id = 2; select :id</c> asks for the first and uses 2 for the second.
    /// </summary>
    /// <param name="preamble">The buffer above the run, for its <c>@set</c> lines and for line numbers.</param>
    public static VariablePlan Plan(ISqlDialect dialect, string sql, string? preamble = null)
    {
        preamble ??= "";
        var own = ScriptDirectives.Find(dialect, sql);
        var above = ScriptDirectives.Find(dialect, preamble);
        var blanked = ScriptDirectives.Blank(sql, own);
        // An `@set x =` with no value is removed from the SQL like any other, but governs nothing: x is asked for.
        var withValue = own.Select((d, i) => (d, i)).Where(x => x.d.HasValue).ToList();
        var refs = SqlVariables.Find(dialect, blanked);
        var firstLine = LineOf(preamble, preamble.Length);

        var lastAbove = new Dictionary<string, ScriptDirective>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in above.Where(d => d.HasValue)) lastAbove[d.Name] = d;

        var slots = new List<VariableSlot>();
        var slotFor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // governing source → slot
        var perName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // slots per name, for placeholders
        var directiveSlot = new Dictionary<int, int>();                                // own directive index → slot
        var uses = new List<(SqlVariableRef, int)>();

        foreach (var r in refs)
        {
            var governing = -1;
            for (var j = withValue.Count - 1; j >= 0; j--)
                if (withValue[j].d.Start < r.Start && withValue[j].d.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase))
                { governing = withValue[j].i; break; }

            var key = governing >= 0 ? $"script:{governing}"
                : lastAbove.ContainsKey(r.Name) ? $"above:{r.Name}"
                : $"prompt:{r.Name}";
            if (!slotFor.TryGetValue(key, out var slot))
            {
                var count = perName.TryGetValue(r.Name, out var n) ? n + 1 : 1;
                perName[r.Name] = count;
                var placeholder = SqlVariables.PlaceholderName(r.Name) + (count == 1 ? "" : $"__{count}");
                slots.Add(governing >= 0
                    ? new VariableSlot(r.Name, placeholder, VariableSource.Script, own[governing].Value, own[governing].Kind,
                        firstLine + LineOf(sql, own[governing].Start) - 1, r.Quoted)
                    : lastAbove.TryGetValue(r.Name, out var a)
                        ? new VariableSlot(r.Name, placeholder, VariableSource.Above, a.Value, a.Kind, LineOf(preamble, a.Start), r.Quoted)
                        : new VariableSlot(r.Name, placeholder, VariableSource.Prompt, null, QueryParameterKind.Text, null, r.Quoted));
                slot = slots.Count - 1;
                slotFor[key] = slot;
                if (governing >= 0) directiveSlot[governing] = slot;
            }
            else if (r.Quoted && !slots[slot].Quoted)
            {
                slots[slot] = slots[slot] with { Quoted = true };
            }
            uses.Add((r, slot));
        }

        var directives = own.Select((d, i) => (d, directiveSlot.TryGetValue(i, out var s) ? (int?)s : null)).ToList();
        return new VariablePlan(sql, blanked, slots, uses, directives);
    }

    /// <summary>
    /// Bind <paramref name="plan"/>: each slot gets its <c>@set</c> value, or the text in <paramref name="texts"/>
    /// at its index when one is given — the prompt's answer, or an override of a <c>@set</c> value for this run —
    /// read by <see cref="ScriptDirectives.ParseValue"/>, the same rules as after <c>=</c>. A slot written
    /// <c>:'name'</c> binds as text whatever its value looks like. Throws if a slot ends up with no value — a
    /// prompt slot without text, or blank text anywhere (<see cref="ScriptDirectives.IsGiven"/>): the caller
    /// checks with <see cref="FirstMissing"/> first.
    /// </summary>
    public static PreparedRun Bind(ISqlDialect dialect, VariablePlan plan, IReadOnlyList<string>? texts = null)
    {
        var values = new List<(string? Value, QueryParameterKind Kind)>();
        for (var i = 0; i < plan.Slots.Count; i++)
        {
            var slot = plan.Slots[i];
            if (texts is not null && i < texts.Count && !ScriptDirectives.IsGiven(texts[i]))
                throw new InvalidOperationException($"No value for :{slot.Name}.");
            var (value, kind) = texts is not null && i < texts.Count ? ScriptDirectives.ParseValue(texts[i])
                : slot.IsPreset ? (slot.Value, slot.Kind)
                : throw new InvalidOperationException($"No value for :{slot.Name}.");
            if (slot.Quoted && kind == QueryParameterKind.Number) kind = QueryParameterKind.Text;
            values.Add((value, kind));
        }

        var sql = new StringBuilder(plan.Sql.Length);
        var copied = 0;
        foreach (var (r, slot) in plan.Uses)
        {
            sql.Append(plan.Sql, copied, r.Start - copied).Append('@').Append(plan.Slots[slot].Placeholder);
            copied = r.Start + r.Length;
        }
        sql.Append(plan.Sql, copied, plan.Sql.Length - copied);

        var parameters = plan.Slots.Select((s, i) => new QueryParameter(s.Placeholder, values[i].Value, values[i].Kind)).ToList();
        var outside = plan.Slots.Select((s, i) => (s, i)).Where(x => x.s.Source != VariableSource.Script)
            .Select(x => new QueryLogParameter(x.s.Name, values[x.i].Value, values[x.i].Kind)).ToList();
        return new PreparedRun(sql.ToString(), parameters, LogText(plan, values), outside);
    }

    /// <summary>The first variable left without a value by <paramref name="texts"/> (a blank answer, or a prompt
    /// slot with no answer at all), or null when every slot has one.</summary>
    public static string? FirstMissing(VariablePlan plan, IReadOnlyList<string>? texts)
    {
        for (var i = 0; i < plan.Slots.Count; i++)
        {
            var given = texts is not null && i < texts.Count ? ScriptDirectives.IsGiven(texts[i]) : plan.Slots[i].IsPreset;
            if (!given) return plan.Slots[i].Name;
        }
        return null;
    }

    /// <summary>
    /// The run as written, with each of its own <c>@set</c> lines rewritten to the value it actually gave — so a
    /// value overridden in the prompt is what the log says ran, and reopening it runs that again. A line nothing
    /// used stays as written.
    /// </summary>
    private static string LogText(VariablePlan plan, IReadOnlyList<(string? Value, QueryParameterKind Kind)> values)
    {
        var sb = new StringBuilder(plan.Text.Length);
        var copied = 0;
        foreach (var (d, slot) in plan.Directives)
        {
            sb.Append(plan.Text, copied, d.Start - copied);
            var line = plan.Text.Substring(d.Start, d.Length);
            if (slot is { } s && ScriptDirectives.FormatValue(values[s].Value, values[s].Kind) is { } written)
                line = line[..(line.Length - line.TrimStart().Length)] + $"@set {d.Name} = {written}";
            sb.Append(line);
            copied = d.End;
        }
        sb.Append(plan.Text, copied, plan.Text.Length - copied);
        return sb.ToString().Trim();
    }

    /// <summary>The 1-based line <paramref name="offset"/> falls on.</summary>
    private static int LineOf(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }
}
