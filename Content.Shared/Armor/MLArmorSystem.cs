using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.Inventory;
using Content.Shared.Verbs;
using Content.Shared._Shitmed.Targeting;
using Robust.Shared.Utility;
using System.Linq;

namespace Content.Shared.Armor;

/// <summary>
///     Answers <see cref="MLZoneArmorEvent"/> relays for worn ML armor pieces:
///     an item contributes its rating when its <see cref="MLArmorComponent.Coverage"/>
///     contains the struck zone. The best (highest) rating wins.
///     Also adds a detailed examine readout: covered zones + how hard the armor
///     is to punch through.
/// </summary>
public sealed class MLArmorSystem : EntitySystem
{
    [Dependency] private readonly ExamineSystemShared _examine = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<MLArmorComponent, InventoryRelayedEvent<MLZoneArmorEvent>>(OnZoneRelay);
        SubscribeLocalEvent<MLArmorComponent, InventoryRelayedEvent<DamageModifyEvent>>(OnDamageModify);
        SubscribeLocalEvent<MLArmorComponent, GetVerbsEvent<ExamineVerb>>(OnArmorVerbExamine);
    }

    private void OnZoneRelay(Entity<MLArmorComponent> ent, ref InventoryRelayedEvent<MLZoneArmorEvent> args)
    {
        var comp = ent.Comp;
        if (comp.Rating <= 0f)
            return;

        if ((comp.Coverage & args.Args.Zone) == 0)
            return;

        if (comp.Rating > args.Args.Rating)
        {
            args.Args.Rating = comp.Rating;
            args.Args.Found = true;
        }
    }

    /// <summary>
    ///     Applies basic damage-type resists (<see cref="MLArmorComponent.Resists"/>) to damage
    ///     landing on covered zones. Damage without a zone (environment, explosions) counts as covered.
    ///     Projectile damage was already scaled by the threshold mechanic in
    ///     <see cref="MLPenetration.ApplyPenetration"/>, so piercing resists would double-dip.
    /// </summary>
    private void OnDamageModify(Entity<MLArmorComponent> ent, ref InventoryRelayedEvent<DamageModifyEvent> args)
    {
        if (ent.Comp.Resists is not { } resists)
            return;

        if (resists.Coefficients.Count == 0 && resists.FlatReduction.Count == 0)
            return;

        if (args.Args.TargetPart is { } zone && (ent.Comp.Coverage & zone) == 0)
            return; // The struck zone is not protected by this piece.

        args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage, resists);
    }

    private void OnArmorVerbExamine(EntityUid uid, MLArmorComponent component, GetVerbsEvent<ExamineVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        _examine.AddDetailedExamineVerb(args, component, GetArmorExamine(component),
            Loc.GetString("ml-armor-verb-text"), "/Textures/Interface/VerbIcons/dot.svg.192dpi.png",
            Loc.GetString("ml-armor-verb-message"));
    }

    private FormattedMessage GetArmorExamine(MLArmorComponent component)
    {
        var msg = new FormattedMessage();
        msg.AddMarkup(Loc.GetString("ml-armor-examine-header"));

        var zones = component.Coverage == TargetBodyPart.All
            ? Loc.GetString("body-part-All")
            : string.Join(", ", SharedTargetingSystem.GetValidParts()
                .Where(p => (component.Coverage & p) != 0)
                .Select(p => Loc.GetString("body-part-" + p)));

        msg.PushNewline();
        msg.AddMarkup(Loc.GetString("ml-armor-coverage", ("zones", zones)));

        var tier = component.Rating switch
        {
            < 0.35f => Loc.GetString("ml-armor-tier-light"),
            < 0.55f => Loc.GetString("ml-armor-tier-medium"),
            < 0.75f => Loc.GetString("ml-armor-tier-heavy"),
            _ => Loc.GetString("ml-armor-tier-fortified"),
        };
        msg.PushNewline();
        msg.AddMarkup(Loc.GetString("ml-armor-rating", ("tier", tier)));

        if (component.Resists is { } resists && resists.Coefficients.Count > 0)
        {
            msg.PushNewline();
            msg.AddMarkup(Loc.GetString("ml-armor-resists-header"));
            foreach (var (type, coeff) in resists.Coefficients)
            {
                msg.PushNewline();
                msg.AddMarkup(Loc.GetString("ml-armor-resist-line",
                    ("type", Loc.GetString("armor-damage-type-" + type.ToLower())),
                    ("value", MathF.Round((1f - coeff) * 100))));
            }
        }

        return msg;
    }
}
