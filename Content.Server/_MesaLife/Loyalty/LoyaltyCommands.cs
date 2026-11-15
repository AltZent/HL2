using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared._MesaLife.Loyalty;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._MesaLife.Loyalty;

/// <summary>
///     Admin command to add or remove loyalty (civil status) points.
///     Doubles as the future CP verdict interface until player CP exists.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class AddLoyalCommand : LocalizedCommands
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public override string Command => "addloyal";
    public override string Description => "Add or remove loyalty (civil status) points for a player.";
    public override string Help => "addloyal <username> <delta> [reason]";

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            var names = _playerManager.Sessions.OrderBy(c => c.Name).Select(c => c.Name).ToArray();
            return CompletionResult.FromHintOptions(names, "<username>");
        }

        return CompletionResult.Empty;
    }

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Loc.GetString("shell-wrong-arguments-number"));
            return;
        }

        if (!_playerManager.TryGetSessionByUsername(args[0], out var session))
        {
            shell.WriteError(Loc.GetString("shell-target-player-does-not-exist"));
            return;
        }

        if (!int.TryParse(args[1], out var delta))
        {
            shell.WriteError(Loc.GetString("loyalty-invalid-delta"));
            return;
        }

        var reason = args.Length > 2 ? string.Join(' ', args.Skip(2)) : "manual";
        var loyalty = _entities.EntitySysManager.GetEntitySystem<LoyaltySystem>();
        loyalty.AddPoints(session, delta, reason);

        var points = loyalty.GetPoints(session);
        if (points is not { } value)
        {
            shell.WriteError(Loc.GetString("loyalty-get-none", ("name", session.Name)));
            return;
        }

        var tier = LoyaltyTiers.GetTier(value, _prototypes);
        shell.WriteLine(Loc.GetString("loyalty-add-success",
            ("name", session.Name),
            ("points", value),
            ("deltaStr", delta >= 0 ? $"+{delta}" : delta.ToString()),
            ("tier", Loc.GetString($"loyalty-tier-{tier.ID}"))));
    }
}

/// <summary>
///     Admin command to read a player's current loyalty points and tier.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class GetLoyalCommand : LocalizedCommands
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public override string Command => "getloyal";
    public override string Description => "Show a player's current loyalty (civil status) points.";
    public override string Help => "getloyal <username>";

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            var names = _playerManager.Sessions.OrderBy(c => c.Name).Select(c => c.Name).ToArray();
            return CompletionResult.FromHintOptions(names, "<username>");
        }

        return CompletionResult.Empty;
    }

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Loc.GetString("shell-wrong-arguments-number"));
            return;
        }

        if (!_playerManager.TryGetSessionByUsername(args[0], out var session))
        {
            shell.WriteError(Loc.GetString("shell-target-player-does-not-exist"));
            return;
        }

        var loyalty = _entities.EntitySysManager.GetEntitySystem<LoyaltySystem>();
        var points = loyalty.GetPoints(session);

        if (points is not { } value)
        {
            shell.WriteLine(Loc.GetString("loyalty-get-none", ("name", session.Name)));
            return;
        }

        var tier = LoyaltyTiers.GetTier(value, _prototypes);
        shell.WriteLine(Loc.GetString("loyalty-get-success",
            ("name", session.Name),
            ("points", value),
            ("tier", Loc.GetString($"loyalty-tier-{tier.ID}"))));
    }
}
