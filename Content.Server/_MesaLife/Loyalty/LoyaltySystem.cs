using Content.Server.Administration.Managers;
using Content.Server.Administration.Logs;
using Content.Server.GameTicking;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared._MesaLife.Loyalty;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._MesaLife.Loyalty;

/// <summary>
///     Persistent loyalty points ("civil status") for MesaLife, in the style of
///     Project Synapse. Points are stored as a playtime tracker
///     (<see cref="LoyaltyConstants.TrackerId"/>, 1 point = 1 minute) so they use
///     the existing per-account persistence, auto-saving and client sync.
///     Tier thresholds are data-driven <c>loyaltyTier</c> prototypes.
/// </summary>
public sealed class LoyaltySystem : EntitySystem
{
    [Dependency] private readonly PlayTimeTrackingManager _playtime = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly GameTicker _ticker = default!;

    /// <summary>Accounts that already paid the round decay this round.</summary>
    private readonly HashSet<NetUserId> _decayedThisRound = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawn);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<ExaminedEvent>(OnExamine);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _decayedThisRound.Clear();
    }

    private void OnPlayerSpawn(PlayerSpawnCompleteEvent args)
    {
        var points = GetPoints(args.Player);

        // First-ever spawn: grant the starting civil status.
        if (points == null)
        {
            AddPoints(args.Player, LoyaltyConstants.StartPoints, "initial civil status", log: false);
            _decayedThisRound.Add(args.Player.UserId);
            return;
        }

        // Living in a Combine city isn't free: decay once per round per account.
        if (_decayedThisRound.Add(args.Player.UserId))
            AddPoints(args.Player, LoyaltyConstants.DecayPerRound, "round decay", log: false);
    }

    /// <summary>
    ///     Current loyalty points for a session, or null if not loaded yet
    ///     (e.g. a brand new account that has never spawned).
    /// </summary>
    public int? GetPoints(ICommonSession session)
    {
        if (!_playtime.TryGetTrackerTime(session, LoyaltyConstants.TrackerId, out var time) || time is not { } value)
            return null;

        return (int) value.TotalMinutes;
    }

    /// <summary>
    ///     Current loyalty points of the player controlling an entity, or null.
    /// </summary>
    public int? GetPoints(EntityUid mob)
    {
        return _player.TryGetSessionByEntity(mob, out var session) ? GetPoints(session) : null;
    }

    /// <summary>
    ///     Adds (or removes, with a negative delta) loyalty points for a player.
    ///     The result is clamped at <see cref="LoyaltyConstants.CapPoints"/> from
    ///     above; negative values are allowed on purpose.
    /// </summary>
    public void AddPoints(ICommonSession session, int delta, string reason, bool log = true)
    {
        if (!_playtime.TryGetTrackerTimes(session, out _))
        {
            // Playtime data is not loaded yet; dropping this would corrupt nothing, but warn anyway.
            Logger.GetSawmill("mesalife.loyalty").Warning($"Loyalty update for unloaded session {session.Name} was dropped.");
            return;
        }

        var current = GetPoints(session) ?? 0;
        var target = Math.Min(current + delta, LoyaltyConstants.CapPoints);

        // Stored as minutes: 1 point = 1 minute (see LoyaltyConstants.TrackerId).
        _playtime.AddTimeToTracker(session, LoyaltyConstants.TrackerId, TimeSpan.FromMinutes(target - current));
        _playtime.QueueSendTimers(session);

        if (log)
        {
            _adminLog.Add(LogType.AdminMessage, LogImpact.High,
                $"Loyalty: {session.Name} ({session.UserId}) round {_ticker.RoundId}: {current} -> {target} ({delta:+#;-#;0}) [{reason}]");
        }
    }

    /// <summary>Adds points to the player controlling an entity, if any.</summary>
    public void AddPoints(EntityUid mob, int delta, string reason, bool log = true)
    {
        if (_player.TryGetSessionByEntity(mob, out var session))
            AddPoints(session, delta, reason, log);
    }

    private void OnExamine(ExaminedEvent args)
    {
        if (!_player.TryGetSessionByEntity(args.Examined, out var session))
            return;

        var points = GetPoints(session);
        if (points == null)
            return;

        var tier = LoyaltyTiers.GetTier(points.Value, _prototypes);
        var tierName = Loc.GetString($"loyalty-tier-{tier.ID}");

        // Exact numbers are only shown to admins for now; CP players will get
        // this later via their ID access.
        if (_admin.IsAdmin(session))
        {
            args.PushMarkup(Loc.GetString("loyalty-examine-admin",
                ("tier", tierName),
                ("color", tier.Color.ToHexNoAlpha()),
                ("points", points.Value)));
        }
        else
        {
            args.PushMarkup(Loc.GetString("loyalty-examine",
                ("tier", tierName),
                ("color", tier.Color.ToHexNoAlpha())));
        }
    }
}
