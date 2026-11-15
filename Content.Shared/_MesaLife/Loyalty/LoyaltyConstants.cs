namespace Content.Shared._MesaLife.Loyalty;

/// <summary>
///     Constants of the MesaLife loyalty ("civil status") system.
/// </summary>
public static class LoyaltyConstants
{
    /// <summary>
    ///     The playtime tracker used to persist loyalty points.
    ///     Points are stored as minutes (1 point = 1 minute) so they piggyback
    ///     on the existing per-account playtime persistence, auto-saving and
    ///     client synchronization. A job/trait requirement can gate on this
    ///     tracker via CharacterPlaytimeRequirement (100 points = min 100).
    /// </summary>
    public const string TrackerId = "MLLoyalty";

    /// <summary>Points granted on a player's very first spawn.</summary>
    public const int StartPoints = 50;

    /// <summary>Maximum possible loyalty points.</summary>
    public const int CapPoints = 1000;

    /// <summary>Points removed per round (once per account per round, on first spawn).</summary>
    public const int DecayPerRound = -5;
}
