using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.HeightAdjust;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Movement.Systems;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Physics;
using Robust.Shared.Prototypes;

namespace Content.Shared._MesaLife.Physique;

/// <summary>
///     Converts a character's height and weight into stat modifiers:
///     <list type="bullet">
///         <item>Tall → more melee damage, slower movement.</item>
///         <item>Short → faster movement, less melee damage.</item>
///         <item>Heavy → more melee damage, slower movement.</item>
///         <item>Light → faster movement, less melee damage.</item>
///     </list>
///     Default (or near-default) values produce no effects at all.
///     Every active buff/debuff is readable through examine.
/// </summary>
public sealed class PhysiqueSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movement = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HumanoidAppearanceComponent, HeightAdjustedEvent>(OnHeightAdjusted);
        SubscribeLocalEvent<PhysiqueComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshSpeed);
        SubscribeLocalEvent<PhysiqueComponent, MeleeHitEvent>(OnMeleeHit);
        SubscribeLocalEvent<PhysiqueComponent, ExaminedEvent>(OnExamine);
    }

    private void OnHeightAdjusted(EntityUid uid, HumanoidAppearanceComponent humanoid, HeightAdjustedEvent args)
    {
        if (!_proto.TryIndex<SpeciesPrototype>(humanoid.Species, out var species))
            return;

        // Height: the profile multiplier relative to the species default (1.0 = default).
        var heightRatio = species.DefaultHeight > 0 ? humanoid.Height / species.DefaultHeight : 1f;
        var heightCm = species.AverageHeight * humanoid.Height;

        // Weight: π·(radius·avg)²·density — exactly the character editor formula.
        // HeightAdjustSystem scales fixture radii by the average scale but never
        // touches density, so the ratio of masses is (avg / defaultAvg)².
        var avg = (humanoid.Width + humanoid.Height) / 2f;
        var defaultAvg = (species.DefaultWidth + species.DefaultHeight) / 2f;
        var weightRatio = defaultAvg > 0 ? (avg / defaultAvg) * (avg / defaultAvg) : 1f;

        float weightKg;
        if (_proto.TryIndex<EntityPrototype>(species.Prototype, out var bodyProto)
            && bodyProto.TryGetComponent<FixturesComponent>(out var fixtureData)
            && fixtureData.Fixtures.TryGetValue("fix1", out var fixture))
        {
            var radius = fixture.Shape.Radius;
            var density = fixture.Density;
            weightKg = MathF.PI * MathF.Pow(radius * avg, 2) * density;

            var defaultWeightKg = MathF.PI * MathF.Pow(radius * defaultAvg, 2) * density;
            var physique = EnsureComp<PhysiqueComponent>(uid);
            ApplyPhysique(uid, physique, heightRatio, weightRatio, heightCm, weightKg,
                species.AverageHeight, defaultWeightKg);
            return;
        }

        // No species fixture: fall back to a generic 71 kg baseline.
        var fallbackPhysique = EnsureComp<PhysiqueComponent>(uid);
        ApplyPhysique(uid, fallbackPhysique, heightRatio, weightRatio, heightCm, 71f * weightRatio,
            species.AverageHeight, 71f);
    }

    private void ApplyPhysique(EntityUid uid, PhysiqueComponent physique,
        float heightRatio, float weightRatio,
        float heightCm, float weightKg, float avgHeightCm, float avgWeightKg)
    {
        var heightAxis = PhysiqueRules.HeightAxis(heightRatio);
        var weightAxis = PhysiqueRules.WeightAxis(weightRatio);

        var changed = !physique.HeightCm.Equals(heightCm)
            || !physique.WeightKg.Equals(weightKg)
            || physique.HeightAxis != heightAxis
            || physique.WeightAxis != weightAxis;

        physique.HeightCm = heightCm;
        physique.AvgHeightCm = avgHeightCm;
        physique.WeightKg = weightKg;
        physique.AvgWeightKg = avgWeightKg;
        physique.HeightAxis = heightAxis;
        physique.WeightAxis = weightAxis;

        if (changed)
        {
            Dirty(uid, physique);
            _movement.RefreshMovementSpeedModifiers(uid);
        }
    }

    private void OnRefreshSpeed(EntityUid uid, PhysiqueComponent physique, RefreshMovementSpeedModifiersEvent args)
    {
        var coefficient = PhysiqueRules.SpeedCoefficient(physique.HeightAxis, physique.WeightAxis);
        if (coefficient == 1f)
            return;

        args.ModifySpeed(coefficient, coefficient);
    }

    private void OnMeleeHit(EntityUid uid, PhysiqueComponent physique, MeleeHitEvent args)
    {
        var coefficient = PhysiqueRules.MeleeCoefficient(physique.HeightAxis, physique.WeightAxis);
        if (coefficient == 1f)
            return;

        args.ModifiersList.Add(PhysiqueRules.MakeMeleeModifierSet(coefficient));
    }

    private void OnExamine(EntityUid uid, PhysiqueComponent physique, ExaminedEvent args)
    {
        // ML: in-game examine shows only the build (height/weight) line.
        // Buff/debuff effect lines are lobby-only (see HumanoidProfileEditor).
        var height = (int) MathF.Round(physique.HeightCm);
        var weight = (int) MathF.Round(physique.WeightKg);

        args.PushMarkup(Loc.GetString("physique-examine-header",
            ("height", height), ("weight", weight)));
    }
}
