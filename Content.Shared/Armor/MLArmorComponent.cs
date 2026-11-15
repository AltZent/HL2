using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.Inventory;
using Content.Shared.Projectiles;
using Content.Shared._Shitmed.Targeting;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Armor;

/// <summary>
///     ML: threshold-style armor rating used for armor-penetration checks.
///     A projectile with <see cref="ProjectileComponent.Penetration"/> >= <see cref="Rating"/>
///     deals full damage; otherwise damage is scaled by penetration / rating (clamped).
///     On a mob this is whole-body armor; on clothing the protected zones are set
///     with <see cref="Coverage"/> (e.g. a vest covers the torso, a helmet the head).
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class MLArmorComponent : Component
{
    /// <summary>
    ///     Armor rating from 0 to 1. 0 = no armor, 1 = nearly impenetrable.
    /// </summary>
    [DataField, AutoNetworkedField]
    [ViewVariables(VVAccess.ReadWrite)]
    public float Rating;

    /// <summary>
    ///     Which body zones this armor protects. Default: all zones (whole-body armor,
    ///     as on ML creatures). Clothing should list its covered zones explicitly,
    ///     e.g. a vest: "Torso, Groin", a helmet: "Head".
    /// </summary>
    [DataField]
    public TargetBodyPart Coverage = TargetBodyPart.All;

    /// <summary>
    ///     Basic damage-type resists (melee, heat, shock...) applied to covered zones.
    ///     Projectiles are separately governed by the <see cref="Rating"/> threshold mechanic,
    ///     so piercing should usually NOT be listed here (it would stack with it).
    /// </summary>
    [DataField]
    public DamageModifierSet? Resists;

    /// <summary>
    ///     Minimum damage factor a failed penetration check can produce.
    ///     Even heavy armor never fully negates a bullet (hard floor).
    /// </summary>
    public const float MinFactor = 0.15f;
}

/// <summary>
///     Relayed into the target's inventory to collect the best ML armor rating
///     that covers the struck body zone. Worn items with an
///     <see cref="MLArmorComponent"/> answer this event.
/// </summary>
public sealed class MLZoneArmorEvent : IInventoryRelayEvent
{
    public SlotFlags TargetSlots => SlotFlags.All;

    /// <summary>The struck body zone.</summary>
    public TargetBodyPart Zone { get; }

    /// <summary>Highest armor rating found among covering items.</summary>
    public float Rating;

    /// <summary>Whether any covering item was found.</summary>
    public bool Found;

    public MLZoneArmorEvent(TargetBodyPart zone)
    {
        Zone = zone;
    }
}

/// <summary>
///     ML armor penetration math, shared between server and prediction.
///
///     Resolution order per hit:
///     1. <see cref="MLArmorComponent"/> directly on the target (mobs) - uniform
///        whole-body rating.
///     2. Worn ML armor - the highest rating among items whose
///        <see cref="MLArmorComponent.Coverage"/> contains the struck zone
///        (helmet protects the head, vest the torso, etc.).
///     3. Vanilla <see cref="ArmorComponent"/> directly on the target (no zone data) -
///        rating derived per damage type from its coefficients.
///     4. No armor data - full damage.
///     Vanilla clothing %-reduction continues to apply afterwards through the regular
///     inventory relay, so the two layers stay consistent.
/// </summary>
public static class MLPenetration
{
    /// <summary>
    ///     Damage factor for a projectile hitting an armored target.
    ///     penetration >= rating -> 1.0 (armor pierced).
    ///     Otherwise factor = penetration / rating, clamped to [MinFactor, 1).
    /// </summary>
    public static float GetPenetrationFactor(float penetration, float rating)
    {
        if (penetration <= 0f || rating <= 0f)
            return 1f;

        if (penetration >= rating)
            return 1f;

        var factor = penetration / rating;
        return Math.Clamp(factor, MLArmorComponent.MinFactor, 1f);
    }

    /// <summary>
    ///     Applies armor penetration scaling to incoming projectile damage.
    ///     Returns the damage unmodified when either side lacks data.
    ///
    ///     Close-range rule: point-blank shots concentrate projectile mass and punch
    ///     through armor much better (shotgun buckshot in the face vs at 10 tiles).
    ///     Effective penetration is boosted up to x3 at contact range, linearly
    ///     fading to x1 at 4+ tiles.
    /// </summary>
    /// <param name="zone">The struck body zone, if the projectile resolved one.</param>
    public static DamageSpecifier ApplyPenetration(
        DamageSpecifier damage,
        EntityUid projectile,
        EntityUid target,
        TargetBodyPart? zone,
        IEntityManager entMan,
        InventorySystem inventory,
        IPrototypeManager proto)
    {
        if (damage.Empty)
            return damage;

        if (!entMan.TryGetComponent<ProjectileComponent>(projectile, out var proj) || proj.Penetration <= 0f)
            return damage;

        // Close-range momentum boost: effective penetration scales with proximity to the source.
        var effPen = proj.Penetration;
        var source = proj.Weapon ?? proj.Shooter;
        if (source is { } src && entMan.EntityExists(src))
        {
            var dist = (entMan.GetComponent<TransformComponent>(target).WorldPosition
                        - entMan.GetComponent<TransformComponent>(src).WorldPosition).Length();
            var boost = Math.Clamp(3f - dist / 2f, 1f, 3f); // 1.0 at 4+ tiles, up to 3.0 point blank
            effPen = Math.Clamp(proj.Penetration * boost, 0f, 1f);
        }

        // 1. Explicit ML rating on the target itself - uniform whole-body armor.
        if (entMan.TryGetComponent<MLArmorComponent>(target, out var hlArmor) && hlArmor.Rating > 0f)
        {
            var factor = GetPenetrationFactor(effPen, hlArmor.Rating);
            return factor < 1f ? damage * factor : damage;
        }

        // 2. Worn ML armor covering the struck zone - modular coverage.
        if (zone is { } z && entMan.TryGetComponent<InventoryComponent>(target, out var inv))
        {
            var zoneEv = new MLZoneArmorEvent(z);
            inventory.RelayEvent((target, inv), ref zoneEv);
            if (zoneEv.Found)
            {
                var factor = GetPenetrationFactor(effPen, zoneEv.Rating);
                return factor < 1f ? damage * factor : damage;
            }

            // The zone is not covered by any worn ML armor - it is unarmored.
            return damage;
        }

        // 3. Vanilla armor directly on the target (mobs without zone armor) - per-type rating.
        if (!entMan.TryGetComponent<ArmorComponent>(target, out var armor))
            return damage;

        var mods = armor.Modifiers;
        if (mods.Coefficients.Count == 0)
            return damage;

        var result = new DamageSpecifier();
        foreach (var (type, value) in damage.DamageDict)
        {
            var coeff = GetCoefficient(mods, type, proto);
            if (coeff is null || coeff.Value >= 1f)
            {
                // Armor does not resist this damage type at all.
                result.DamageDict[type] = value;
                continue;
            }

            var rating = 1f - coeff.Value;
            var factor = GetPenetrationFactor(effPen, rating);
            result.DamageDict[type] = value * factor;
        }

        return result;
    }

    /// <summary>
    ///     Finds the vanilla armor coefficient protecting against a damage type:
    ///     first by the type id itself, then by the damage group containing it.
    /// </summary>
    private static float? GetCoefficient(DamageModifierSet mods, string damageType, IPrototypeManager proto)
    {
        if (mods.Coefficients.TryGetValue(damageType, out var c))
            return c;

        foreach (var group in proto.EnumeratePrototypes<DamageGroupPrototype>())
        {
            if (group.DamageTypes.Contains(damageType) && mods.Coefficients.TryGetValue(group.ID, out var gc))
                return gc;
        }

        return null;
    }
}
