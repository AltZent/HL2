using Content.Shared.Damage;
using Robust.Shared.Localization;

namespace Content.Shared._MesaLife.Physique;

/// <summary>
///     Pure physique rules, shared between the runtime examine readout (server)
///     and the character editor preview (client). Default (or near-default)
///     height/weight produce no effects at all.
/// </summary>
public static class PhysiqueRules
{
    /// <summary>±6% height around the species default = no effect.</summary>
    public const float HeightNeutral = 0.06f;

    /// <summary>Height deviation of ±15% or more = strong tier.</summary>
    public const float HeightStrong = 0.15f;

    /// <summary>±15% weight around the species default = no effect.</summary>
    public const float WeightNeutral = 0.15f;

    /// <summary>Weight deviation of ±40% or more = strong tier.</summary>
    public const float WeightStrong = 0.40f;

    /// <summary>Melee damage change per signed axis step (5% per step).</summary>
    public const float MeleePerStep = 0.05f;

    /// <summary>Move speed change per signed axis step (4% per step).</summary>
    public const float SpeedPerStep = 0.04f;

    private const float MeleeClamp = 0.35f; // total melee modifier in [0.65 .. 1.35]
    private const float SpeedClamp = 0.30f; // total speed modifier in [0.70 .. 1.30]

    /// <summary>
    ///     Signed height axis from the height ratio (actual / species default):
    ///     -2 very short .. 0 neutral .. +2 very tall.
    /// </summary>
    public static sbyte HeightAxis(float heightRatio)
    {
        if (heightRatio >= 1f + HeightStrong)
            return 2;
        if (heightRatio >= 1f + HeightNeutral)
            return 1;
        if (heightRatio <= 1f - HeightStrong)
            return -2;
        if (heightRatio <= 1f - HeightNeutral)
            return -1;
        return 0;
    }

    /// <summary>
    ///     Signed weight axis from the weight ratio (actual / species default):
    ///     -2 very light .. 0 neutral .. +2 very heavy.
    /// </summary>
    public static sbyte WeightAxis(float weightRatio)
    {
        if (weightRatio >= 1f + WeightStrong)
            return 2;
        if (weightRatio >= 1f + WeightNeutral)
            return 1;
        if (weightRatio <= 1f - WeightStrong)
            return -2;
        if (weightRatio <= 1f - WeightNeutral)
            return -1;
        return 0;
    }

    /// <summary>
    ///     Total melee damage coefficient. Tall and heavy characters hit harder,
    ///     short and light ones hit softer. Height and weight stack.
    /// </summary>
    public static float MeleeCoefficient(sbyte heightAxis, sbyte weightAxis)
    {
        return Math.Clamp(1f + (heightAxis + weightAxis) * MeleePerStep, 1f - MeleeClamp, 1f + MeleeClamp);
    }

    /// <summary>
    ///     Total movement speed coefficient. Short and light characters move
    ///     faster, tall and heavy ones move slower. Height and weight stack.
    /// </summary>
    public static float SpeedCoefficient(sbyte heightAxis, sbyte weightAxis)
    {
        return Math.Clamp(1f - (heightAxis + weightAxis) * SpeedPerStep, 1f - SpeedClamp, 1f + SpeedClamp);
    }

    /// <summary>
    ///     Builds a damage modifier set that scales blunt/slash/piercing melee
    ///     damage by the given coefficient.
    /// </summary>
    public static DamageModifierSet MakeMeleeModifierSet(float coefficient)
    {
        return new DamageModifierSet
        {
            Coefficients = new Dictionary<string, float>
            {
                { "Blunt", coefficient },
                { "Slash", coefficient },
                { "Piercing", coefficient },
            },
        };
    }

    /// <summary>
    ///     Localized markup lines describing the active buffs/debuffs and their
    ///     cause (actual vs average height/weight). Empty list = no effects.
    /// </summary>
    public static List<string> BuildEffectLines(sbyte heightAxis, sbyte weightAxis, float avgHeightCm, float avgWeightKg)
    {
        var lines = new List<string>();

        if (heightAxis != 0)
        {
            lines.Add(Loc.GetString("physique-examine-line",
                ("effect", GetHeightEffect(heightAxis)),
                ("cause", Loc.GetString("physique-cause-height",
                    ("avg", (int) MathF.Round(avgHeightCm))))));
        }

        if (weightAxis != 0)
        {
            lines.Add(Loc.GetString("physique-examine-line",
                ("effect", GetWeightEffect(weightAxis)),
                ("cause", Loc.GetString("physique-cause-weight",
                    ("avg", (int) MathF.Round(avgWeightKg))))));
        }

        return lines;
    }

    private static string GetHeightEffect(sbyte axis)
    {
        var tier = Math.Abs(axis);
        var melee = (int) MathF.Round(tier * MeleePerStep * 100f);
        var speed = (int) MathF.Round(tier * SpeedPerStep * 100f);

        return axis > 0
            ? Loc.GetString($"physique-tall-{tier}", ("melee", melee), ("speed", speed))
            : Loc.GetString($"physique-short-{tier}", ("melee", melee), ("speed", speed));
    }

    private static string GetWeightEffect(sbyte axis)
    {
        var tier = Math.Abs(axis);
        var melee = (int) MathF.Round(tier * MeleePerStep * 100f);
        var speed = (int) MathF.Round(tier * SpeedPerStep * 100f);

        return axis > 0
            ? Loc.GetString($"physique-heavy-{tier}", ("melee", melee), ("speed", speed))
            : Loc.GetString($"physique-light-{tier}", ("melee", melee), ("speed", speed));
    }
}
