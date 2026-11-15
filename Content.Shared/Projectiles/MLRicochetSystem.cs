using System.Linq;
using System.Numerics;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Utility;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared.Projectiles;

/// <summary>
///     ML: wall ricochet for <see cref="MLRicochetComponent"/> projectiles.
///     Each tick we raycast ahead of the projectile; if a wall is within the
///     tick's travel distance, the velocity is reflected around the struck
///     axis BEFORE the physics contact happens. This way wall hits never
///     reach damage or trigger handlers (no explosion, no deletion) and the
///     orb simply bounces, like a Half-Life 2 dark energy orb.
/// </summary>
public sealed class MLRicochetSystem : EntitySystem
{
    [Dependency] private readonly INetManager _netManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private const CollisionGroup WallMask =
        CollisionGroup.Impassable | CollisionGroup.BulletImpassable;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MLRicochetComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<MLRicochetComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.RemainingBounces = ent.Comp.MaxBounces;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_timing.IsFirstTimePredicted)
            return;

        var query = EntityQueryEnumerator<MLRicochetComponent, PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var body, out var xform))
        {
            if (comp.RemainingBounces <= 0)
                continue;

            var velocity = body.LinearVelocity;
            if (velocity.LengthSquared() < 0.01f)
                continue;

            // HL2: orbs disappear when they fly out under the open sky.
            // Server-authoritative; the client prediction just keeps flying.
            if (_netManager.IsServer && comp.DespawnInOpenSky && IsOpenSky(xform))
            {
                QueueDel(uid);
                continue;
            }

            var mapId = xform.MapID;
            if (mapId == MapId.Nullspace)
                continue;

            var pos = _transform.GetWorldPosition(uid);
            var dir = velocity.Normalized();

            // Look far enough ahead to catch the wall before the projectile
            // tunnels into it during this tick.
            var range = MathF.Max(comp.MinRange, velocity.Length() * frameTime * 2.5f);
            var ray = new CollisionRay(pos, dir, (int) WallMask);
            var hit = _physics.IntersectRay(mapId, ray, range, uid, returnOnFirstHit: false).FirstOrNull();
            if (hit == null)
                continue;

            // Axis-aligned reflection against the struck surface: the normal is
            // the dominant axis of the vector from the hit point to the orb.
            var d = pos - hit.Value.HitPos;
            var n = MathF.Abs(d.X) >= MathF.Abs(d.Y)
                ? new Vector2(MathF.Sign(d.X), 0f)
                : new Vector2(0f, MathF.Sign(d.Y));

            if (n == Vector2.Zero || Vector2.Dot(velocity, n) >= 0f)
                continue;

            _physics.SetLinearVelocity(uid, velocity - 2f * Vector2.Dot(velocity, n) * n, body: body);
            comp.RemainingBounces--;
        }
    }

    private bool IsOpenSky(TransformComponent xform)
    {
        if (xform.GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComp))
            return true; // floating over map space

        var local = xform.LocalPosition;
        var indices = new Vector2i((int) MathF.Floor(local.X), (int) MathF.Floor(local.Y));
        return !gridComp.TryGetTileRef(indices, out var tile) || tile.Tile.IsEmpty;
    }
}
