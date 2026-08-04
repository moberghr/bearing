using System.Collections.Generic;

namespace Bearing.App.Input;

/// <summary>Id → <see cref="KeyCommand"/>. Global/Editor commands register from the window, Grid commands
/// from the results view; the palette reads <see cref="All"/>. Later registration wins on a duplicate id.</summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, KeyCommand> _byId = new();

    public void Register(KeyCommand command) => _byId[command.Id] = command;

    public void RegisterAll(IEnumerable<KeyCommand> commands)
    {
        foreach (var c in commands) Register(c);
    }

    public KeyCommand? Get(string id) => _byId.TryGetValue(id, out var c) ? c : null;

    public IReadOnlyCollection<KeyCommand> All => _byId.Values;
}
