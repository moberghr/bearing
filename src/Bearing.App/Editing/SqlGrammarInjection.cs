using System.Text;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Bearing.App.Editing;

/// <summary>
/// The bundled VS Code SQL grammar has no rule for boolean literals: <c>true</c> and <c>false</c> appear in
/// none of its patterns, so they tokenize as plain text and fall through to the editor foreground while the
/// <c>null</c> beside them is coloured (by <c>(?i:\b(on|off|((is\s+)?not\s+)?null)\b)</c>, which the grammar
/// files under the DDL scope). This restores the pair, as an <b>injection</b> over <c>source.sql</c> rather
/// than a vendored copy of the grammar — a fork would have to be re-merged every time the grammar package
/// moves, for one rule.
/// <para>
/// The scope is <c>constant.language.sql</c>, which is what a boolean literal is and what DarkPlus resolves
/// to the same <c>#569cd6</c> it gives <c>null</c>'s keyword scope — so the two read alike, as they do in
/// DBeaver. A theme that split the two would still be saying something true about them.
/// </para>
/// <para>
/// Wrapping <see cref="RegistryOptions"/> rather than subclassing it: the registry is the grammar/theme
/// locator TextMate asks, and only the two lookups this rule touches are answered here. Everything else is
/// the package's.
/// </para>
/// </summary>
internal sealed class SqlGrammarInjection : IRegistryOptions
{
    /// <summary>The host grammar this injects into — the scope the SQL grammar declares.</summary>
    private const string SqlScope = "source.sql";

    internal const string ScopeName = "bearing.injection.sql-literals";

    /// <summary>The scope the booleans are tagged with, and so the one a theme colours them through.</summary>
    internal const string BooleanScope = "constant.language.sql";

    // "L:" puts these patterns ahead of the host grammar's own, and the two exclusions keep the word "true"
    // inside a string or a comment out of it — an injection selector matches on the scope stack, so those
    // are the two stacks to decline.
    //
    // The word boundaries are "\\b" because this is JSON before it is a regex: a lone "\b" is JSON's
    // backspace escape, and the parser hands Oniguruma a pattern built around U+0008 that can never match.
    // Nothing reports that — not the grammar reader, not the registry, not the tokenizer; the booleans simply
    // stay uncoloured, exactly as they were before this file existed. The scope name is repeated inside the
    // JSON for the same reason: it has to equal ScopeName or the injection is looked up and never applied.
    // Both are what the tokenizer tests pin.
    private const string GrammarJson = """
        {
          "scopeName": "bearing.injection.sql-literals",
          "injectionSelector": "L:source.sql -comment -string",
          "patterns": [
            { "match": "(?i)\\b(?:true|false)\\b", "name": "constant.language.sql" }
          ]
        }
        """;

    internal SqlGrammarInjection(RegistryOptions inner) => Registry = inner;

    /// <summary>The packaged registry underneath. Exposed because the language/scope lookups the install path
    /// needs (<c>GetLanguageByExtension</c>, <c>GetScopeByLanguageId</c>) live on the concrete type rather
    /// than on <see cref="IRegistryOptions"/> — and so the two are one object, never two lazies to desync.</summary>
    internal RegistryOptions Registry { get; }

    public IRawTheme GetTheme(string scopeName) => Registry.GetTheme(scopeName);

    public IRawTheme GetDefaultTheme() => Registry.GetDefaultTheme();

    public IRawGrammar GetGrammar(string scopeName) =>
        scopeName == ScopeName ? ReadInjectionGrammar() : Registry.GetGrammar(scopeName);

    public ICollection<string> GetInjections(string scopeName)
    {
        var inner = Registry.GetInjections(scopeName);
        if (scopeName != SqlScope)
            return inner;

        // Appended, never replacing. The packaged grammar declares none for this scope today, so `inner` is
        // in fact null — which is why the branch above hands its own result straight back rather than
        // normalising it. The `?? []` is what keeps this an append if the package ever grows one.
        return new List<string>(inner ?? []) { ScopeName };
    }

    private static IRawGrammar ReadInjectionGrammar()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(GrammarJson));
        using var reader = new StreamReader(stream);
        return GrammarReader.ReadGrammarSync(reader);
    }
}
