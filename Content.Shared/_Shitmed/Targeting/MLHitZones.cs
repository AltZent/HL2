namespace Content.Shared._Shitmed.Targeting;

/// <summary>
///     ML: positional hit zones for projectiles.
///     A projectile decides which body part it struck from its actual height over
///     the target, instead of always using the shooter's targeting-doll selection.
///     The doll selection still matters: it biases the impact height ("aiming high/low"),
///     but that bias fades with distance so precise part-hits are a close-range skill.
///     Weapon spread naturally degrades precision at range, since it widens the
///     vertical scatter of the projectile.
/// </summary>
public static class MLHitZones
{
    // Height bands over the target (0 = feet, 1 = top of head).
    public const float HeadBand = 0.80f;
    public const float TorsoBand = 0.50f;
    public const float LegsBand = 0.25f;

    /// <summary>
    ///     How much of the aim bias applies at a given distance.
    ///     Full bias up to 6 tiles, fading linearly to nothing at 12 tiles.
    /// </summary>
    public static float GetAimScale(float distance)
    {
        return Math.Clamp(2f - distance / 6f, 0f, 1f);
    }

    /// <summary>
    ///     Vertical aim bias for the selected body part (positive = aim higher).
    /// </summary>
    public static float GetAimBias(TargetBodyPart aimed)
    {
        return aimed switch
        {
            // Head bias kept under 1/3: combined with the 0.62 base in
            // ComputeHitPart this must land above HeadBand (0.80) without
            // pushing arm/hand aims past it.
            TargetBodyPart.Head => 0.30f,
            TargetBodyPart.LeftHand or TargetBodyPart.RightHand => 0.12f,
            TargetBodyPart.LeftArm or TargetBodyPart.RightArm => 0.10f,
            TargetBodyPart.Groin => -0.10f,
            TargetBodyPart.LeftLeg or TargetBodyPart.RightLeg => -0.20f,
            TargetBodyPart.LeftFoot or TargetBodyPart.RightFoot => -0.30f,
            _ => 0f,
        };
    }

    /// <summary>
    ///     Resolves the struck body part from the projectile's relative impact height.
    /// </summary>
    /// <param name="relHeight">Projectile height over the target, 0 = feet, 1 = top of head.</param>
    /// <param name="aimed">The shooter's targeting-doll selection.</param>
    /// <param name="aimScale">0..1, from <see cref="GetAimScale"/>.</param>
    /// <param name="rand">Random source (server-side).</param>
    public static TargetBodyPart ResolveHitPart(float relHeight, TargetBodyPart aimed, float aimScale, System.Random rand)
    {
        var rel = Math.Clamp(relHeight + GetAimBias(aimed) * aimScale + (float) (rand.NextDouble() * 0.1 - 0.05), 0f, 1f);

        if (rel > HeadBand)
            return TargetBodyPart.Head;

        if (rel >= TorsoBand)
        {
            // Chest zone: honors a precise arm/hand aim only at close range.
            if (aimed is TargetBodyPart.LeftArm or TargetBodyPart.RightArm
                or TargetBodyPart.LeftHand or TargetBodyPart.RightHand
                && rand.NextDouble() < aimScale)
                return aimed;

            return TargetBodyPart.Torso;
        }

        if (rel >= LegsBand)
        {
            if (aimed is TargetBodyPart.LeftLeg or TargetBodyPart.RightLeg
                or TargetBodyPart.LeftFoot or TargetBodyPart.RightFoot
                && rand.NextDouble() < aimScale)
                return aimed;

            return rand.NextDouble() < 0.5 ? TargetBodyPart.LeftLeg : TargetBodyPart.RightLeg;
        }

        if (aimed is TargetBodyPart.LeftFoot or TargetBodyPart.RightFoot && rand.NextDouble() < aimScale)
            return aimed;

        return rand.NextDouble() < 0.5 ? TargetBodyPart.LeftFoot : TargetBodyPart.RightFoot;
    }
}
