using System.Numerics;
using System.Collections.Generic;
using Content.Shared.Body.Systems;
using Content.Shared.Armor;
using Content.Shared.Damage;
using Content.Shared.Administration.Logs;
using Content.Shared.Effects;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.IdentityManagement;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Inventory;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Camera;
using Content.Shared.Database;
using Content.Shared._RMC14.Weapons.Ranged.Prediction;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;

namespace Content.Shared.Projectiles;

public abstract partial class SharedProjectileSystem : EntitySystem
{
    public const string ProjectileFixture = "projectile";

    [Dependency] private readonly INetManager _netManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly SharedColorFlashEffectSystem _color = default!;
    [Dependency] private readonly DamageableSystem _damageableSystem = default!;
    [Dependency] private readonly SharedGunSystem _guns = default!;
    [Dependency] private readonly SharedCameraRecoilSystem _sharedCameraRecoil = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ProjectileComponent, StartCollideEvent>(OnStartCollide);
        SubscribeLocalEvent<ProjectileComponent, PreventCollideEvent>(PreventCollision);
        SubscribeLocalEvent<EmbeddableProjectileComponent, ProjectileHitEvent>(OnEmbedProjectileHit);
        SubscribeLocalEvent<EmbeddableProjectileComponent, ThrowDoHitEvent>(OnEmbedThrowDoHit);
        SubscribeLocalEvent<EmbeddableProjectileComponent, ActivateInWorldEvent>(OnEmbedActivate);
        SubscribeLocalEvent<EmbeddableProjectileComponent, RemoveEmbeddedProjectileEvent>(OnEmbedRemove);
        SubscribeLocalEvent<EmbeddableProjectileComponent, ExaminedEvent>(OnExamined);
    }

    // TODO: rename Embedded to Target in every context
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<EmbeddableProjectileComponent>();
        var curTime = _timing.CurTime;

        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.AutoRemoveTime == null || comp.AutoRemoveTime > curTime)
                continue;

            if (comp.Target is {} targetUid)
                _popup.PopupClient(Loc.GetString("throwing-embed-falloff", ("item", uid)), targetUid, targetUid);

            RemoveEmbed(uid, comp);
        }
    }

    private void OnEmbedActivate(EntityUid uid, EmbeddableProjectileComponent component, ActivateInWorldEvent args)
    {
        // Nuh uh
        if (component.RemovalTime == null)
            return;

        if (args.Handled || !args.Complex || !TryComp<PhysicsComponent>(uid, out var physics) || physics.BodyType != BodyType.Static)
            return;

        args.Handled = true;

        if (component.Target is {} targetUid)
            _popup.PopupClient(Loc.GetString("throwing-embed-remove-alert-owner", ("item", uid), ("other", args.User)),
                args.User, targetUid);

        _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, args.User, component.RemovalTime.Value,
            new RemoveEmbeddedProjectileEvent(), eventTarget: uid, target: uid)
        {
            DistanceThreshold = SharedInteractionSystem.InteractionRange,
            BreakOnMove = true,
            NeedHand = true,
        });
    }

    private void OnEmbedRemove(EntityUid uid, EmbeddableProjectileComponent component, RemoveEmbeddedProjectileEvent args)
    {
        if (args.Cancelled)
            return;

        RemoveEmbed(uid, component, args.User);
    }

    public void RemoveEmbed(EntityUid uid, EmbeddableProjectileComponent component, EntityUid? remover = null)
    {
        component.AutoRemoveTime = null;
        component.Target = null;
        component.TargetBodyPart = null;

        var ev = new RemoveEmbedEvent(remover);
        RaiseLocalEvent(uid, ref ev);

        // Whacky prediction issues.
        if (_netManager.IsClient)
            return;

        if (component.DeleteOnRemove)
        {
            QueueDel(uid);
            return;
        }

        var xform = Transform(uid);
        TryComp<PhysicsComponent>(uid, out var physics);
        _physics.SetBodyType(uid, BodyType.Dynamic, body: physics, xform: xform);
        _transform.AttachToGridOrMap(uid, xform);

        // Reset whether the projectile has damaged anything if it successfully was removed
        if (TryComp<ProjectileComponent>(uid, out var projectile))
        {
            projectile.Shooter = null;
            projectile.Weapon = null;
            projectile.DamagedEntity = false;
        }

        // Land it just coz uhhh yeah
        var landEv = new LandEvent(remover, true);
        RaiseLocalEvent(uid, ref landEv);
        _physics.WakeBody(uid, body: physics);

        // try place it in the user's hand
        if (remover is {} removerUid)
            _hands.TryPickupAnyHand(removerUid, uid);
    }

    private void OnEmbedThrowDoHit(EntityUid uid, EmbeddableProjectileComponent component, ThrowDoHitEvent args)
    {
        if (!component.EmbedOnThrow ||
            HasComp<ThrownItemImmuneComponent>(args.Target))
            return;

        Embed(uid, args.Target, null, component, args.TargetPart);
    }

    private void OnEmbedProjectileHit(EntityUid uid, EmbeddableProjectileComponent component, ref ProjectileHitEvent args)
    {
        Embed(uid, args.Target, args.Shooter, component);

        // Raise a specific event for projectiles.
        if (TryComp(uid, out ProjectileComponent? projectile))
        {
            var ev = new ProjectileEmbedEvent(projectile.Shooter!.Value, projectile.Weapon!.Value, args.Target);
            RaiseLocalEvent(uid, ref ev);
        }
    }

    private void Embed(EntityUid uid, EntityUid target, EntityUid? user, EmbeddableProjectileComponent component, TargetBodyPart? targetPart = null)
    {
        TryComp<PhysicsComponent>(uid, out var physics);
        _physics.SetLinearVelocity(uid, Vector2.Zero, body: physics);
        _physics.SetBodyType(uid, BodyType.Static, body: physics);
        var xform = Transform(uid);
        _transform.SetParent(uid, xform, target);

        if (component.Offset != Vector2.Zero)
        {
            _transform.SetLocalPosition(uid, xform.LocalPosition + xform.LocalRotation.RotateVec(component.Offset),
                xform);
        }

        _audio.PlayPredicted(component.Sound, uid, null);

        component.TargetBodyPart = targetPart;
        var ev = new EmbedEvent(user, target, targetPart);
        RaiseLocalEvent(uid, ref ev);

        if (component.AutoRemoveDuration != 0)
            component.AutoRemoveTime = _timing.CurTime + TimeSpan.FromSeconds(component.AutoRemoveDuration);

        component.Target = target;

        Dirty(uid, component);
    }

    private void OnStartCollide(EntityUid uid, ProjectileComponent component, ref StartCollideEvent args)
    {
        if (args.OurFixtureId != ProjectileFixture || !args.OtherFixture.Hard || component.DamagedEntity || component is { Weapon: null, OnlyCollideWhenShot: true })
            return;

        if (!TryComp(uid, out PhysicsComponent? body))
            return;

        ProjectileCollide((uid, component, body), args.OtherEntity);
    }

    public void ProjectileCollide(Entity<ProjectileComponent, PhysicsComponent> projectile, EntityUid target, bool predicted = false)
    {
        var (uid, component, ourBody) = projectile;

        if (component.DamagedEntity)
        {
            if (_netManager.IsServer && component.DeleteOnCollide)
                QueueDel(uid);

            return;
        }

        var attemptEv = new ProjectileReflectAttemptEvent(uid, component, false);
        RaiseLocalEvent(target, ref attemptEv);
        if (attemptEv.Cancelled)
        {
            SetShooter(uid, component, target);
            return;
        }

        var ev = new ProjectileHitEvent(component.Damage, target, component.Shooter);
        RaiseLocalEvent(uid, ref ev);
        if (ev.Handled)
            return;

        var coordinates = Transform(projectile).Coordinates;
        var otherName = ToPrettyString(target);
        var direction = ourBody.LinearVelocity.Normalized();

        // ML: positional hit zones - decide which body part the projectile actually struck
        // from its height over the target, instead of always using the shooter's doll selection.
        TargetBodyPart? hitPart = null;
        if (_netManager.IsServer && TryComp<TargetingComponent>(target, out _))
            hitPart = ComputeHitPart(uid, target, component);

        // ML: armor penetration check - scale damage when the projectile fails to punch through armor.
        var finalDamage = MLPenetration.ApplyPenetration(ev.Damage, uid, target, hitPart, EntityManager, _inventory, _prototypeManager);

        var modifiedDamage = _netManager.IsServer
            ? _damageableSystem.TryChangeDamage(target, finalDamage, component.IgnoreResistances, origin: component.Shooter, targetPart: hitPart)
            : new DamageSpecifier(finalDamage);
        var deleted = Deleted(target);

        var filter = Filter.Pvs(coordinates, entityMan: EntityManager);
        if (_guns.GunPrediction && TryComp(projectile, out PredictedProjectileServerComponent? serverProjectile))
            filter = filter.RemovePlayer(serverProjectile.Shooter);

        if (modifiedDamage is not null && (EntityManager.EntityExists(component.Shooter) || EntityManager.EntityExists(component.Weapon)))
        {
            if (modifiedDamage.AnyPositive() && !deleted)
                _color.RaiseEffect(Color.Red, new List<EntityUid> { target }, filter);

            var shooterOrWeapon = EntityManager.EntityExists(component.Shooter) ? component.Shooter!.Value : component.Weapon!.Value;

            _adminLogger.Add(LogType.BulletHit,
                HasComp<ActorComponent>(target) ? LogImpact.Extreme : LogImpact.High,
                $"Projectile {ToPrettyString(uid):projectile} shot by {ToPrettyString(shooterOrWeapon):source} hit {otherName:target} and dealt {modifiedDamage.GetTotal():damage} damage");
        }

        if (!deleted)
        {
            _guns.PlayImpactSound(target, modifiedDamage, component.SoundHit, component.ForceSound, filter, projectile);
            _sharedCameraRecoil.KickCamera(target, direction);
        }

        component.DamagedEntity = true;
        Dirty(uid, component);

        if (!predicted && component.DeleteOnCollide && (_netManager.IsServer || IsClientSide(uid)))
            QueueDel(uid);
        else if (_netManager.IsServer && component.DeleteOnCollide)
        {
            var predictedComp = EnsureComp<PredictedProjectileHitComponent>(uid);
            predictedComp.Origin = _transform.GetMoverCoordinates(coordinates);

            var targetCoords = _transform.GetMoverCoordinates(target);
            if (predictedComp.Origin.TryDistance(EntityManager, _transform, targetCoords, out var distance))
                predictedComp.Distance = distance;

            Dirty(uid, predictedComp);
        }

        if ((_netManager.IsServer || IsClientSide(uid)) && component.ImpactEffect != null)
        {
            var impactEffectEv = new ImpactEffectEvent(component.ImpactEffect, GetNetCoordinates(coordinates));
            if (_netManager.IsServer)
                RaiseNetworkEvent(impactEffectEv, filter);
            else
                RaiseLocalEvent(impactEffectEv);
        }
    }

    /// <summary>
    ///     ML: resolves the body part struck by a projectile from geometry.
    ///     Uses the projectile's height over the target (calibrated by the target's
    ///     collision size), the shooter's doll selection as an aim bias that fades
    ///     with distance, and small random scatter.
    /// </summary>
    private TargetBodyPart? ComputeHitPart(EntityUid projectile, EntityUid target, ProjectileComponent component)
    {
        var aabb = _lookup.GetWorldAABB(target);
        if (aabb.Height <= 0f)
            return null;

        var projPos = _transform.GetWorldPosition(projectile);

        // Canonical span estimate: most mob fixtures are much smaller than their sprite.
        var span = Math.Clamp(aabb.Height * 2.6f, 0.9f, 2.6f);
        // Base 0.62 instead of the naive 0.5: gun muzzles sit noticeably below the
        // sprite AABB center (hands + gear), which dragged every hit into the legs
        // band. The offset re-centers torso-height impacts in the torso band.
        var rel = Math.Clamp(0.62f + (projPos.Y - aabb.Center.Y) / span, 0f, 1f);

        var aimed = TargetBodyPart.Torso;
        if (component.Shooter is { } shooter && TryComp<TargetingComponent>(shooter, out var targeting))
            aimed = targeting.Target;

        var aimScale = 0f;
        var source = component.Weapon ?? component.Shooter;
        if (source is { } src && EntityManager.EntityExists(src))
        {
            var dist = (_transform.GetWorldPosition(target) - _transform.GetWorldPosition(src)).Length();
            aimScale = MLHitZones.GetAimScale(dist);
        }

        var rand = new System.Random((int) _timing.CurTick.Value);
        return MLHitZones.ResolveHitPart(rel, aimed, aimScale, rand);
    }

    private void PreventCollision(EntityUid uid, ProjectileComponent component, ref PreventCollideEvent args)
    {
        if (component.IgnoreShooter && (args.OtherEntity == component.Shooter || args.OtherEntity == component.Weapon))
        {
            args.Cancelled = true;
        }
    }

    public void SetShooter(EntityUid id, ProjectileComponent component, EntityUid shooterId)
    {
        if (component.Shooter == shooterId)
            return;

        component.Shooter = shooterId;
        Dirty(id, component);
    }

    private void OnExamined(EntityUid uid, EmbeddableProjectileComponent component, ExaminedEvent args)
    {
        if (!(component.Target is {} target))
            return;

        var targetIdentity = Identity.Entity(target, EntityManager);

        var loc = component.TargetBodyPart == null
            ? Loc.GetString("throwing-examine-embedded",
            ("embedded", uid),
            ("target", targetIdentity))
            : Loc.GetString("throwing-examine-embedded-part",
            ("embedded", uid),
            ("target", targetIdentity),
            ("targetPart", Loc.GetString($"body-part-{component.TargetBodyPart.ToString()}")));

        args.PushMarkup(loc);
    }

    [Serializable, NetSerializable]
    private sealed partial class RemoveEmbeddedProjectileEvent : DoAfterEvent
    {
        public override DoAfterEvent Clone() => this;
    }
}

[Serializable, NetSerializable]
public sealed class ImpactEffectEvent : EntityEventArgs
{
    public string Prototype;
    public NetCoordinates Coordinates;

    public ImpactEffectEvent(string prototype, NetCoordinates coordinates)
    {
        Prototype = prototype;
        Coordinates = coordinates;
    }
}

/// <summary>
/// Raised when an entity is just about to be hit with a projectile but can reflect it
/// </summary>
[ByRefEvent]
public record struct ProjectileReflectAttemptEvent(EntityUid ProjUid, ProjectileComponent Component, bool Cancelled);

/// <summary>
/// Raised when a projectile hits an entity
/// </summary>
[ByRefEvent]
public record struct ProjectileHitEvent(DamageSpecifier Damage, EntityUid Target, EntityUid? Shooter = null)
{
    public bool Handled;
}

/// <summary>
/// Raised after a projectile has dealt it's damage.
/// </summary>
[ByRefEvent]
public record struct AfterProjectileHitEvent(DamageSpecifier Damage, EntityUid Target);
