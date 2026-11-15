using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared._MesaLife.Loyalty;

/// <summary>
///     A civil status tier in the loyalty system ("Civic Status", MesaLife).
///     Tiers are keyed by the minimum number of loyalty points required; the
///     highest tier whose <see cref="MinPoints"/> is satisfied is the player's
///     current tier.
/// </summary>
[Prototype("loyaltyTier")]
public sealed partial class LoyaltyTierPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Minimum loyalty points (inclusive) required for this tier.</summary>
    [DataField]
    public int MinPoints;

    /// <summary>Markup color used when displaying the tier.</summary>
    [DataField]
    public Color Color = Color.White;
}

public static class LoyaltyTiers
{
    /// <summary>
    ///     Resolves the tier for a number of loyalty points. Falls back to the
    ///     lowest tier when points are below every defined minimum.
    /// </summary>
    public static LoyaltyTierPrototype GetTier(int points, IPrototypeManager prototypes)
    {
        LoyaltyTierPrototype? best = null;
        LoyaltyTierPrototype? lowest = null;

        foreach (var tier in prototypes.EnumeratePrototypes<LoyaltyTierPrototype>())
        {
            if (lowest == null || tier.MinPoints < lowest.MinPoints)
                lowest = tier;

            if (points >= tier.MinPoints && (best == null || tier.MinPoints > best.MinPoints))
                best = tier;
        }

        return best ?? lowest!;
    }
}
