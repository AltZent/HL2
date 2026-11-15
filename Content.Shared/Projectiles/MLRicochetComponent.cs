using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Projectiles;

/// <summary>
///     ML: makes a projectile ricochet off walls (grid fixtures) instead of
///     colliding/exploding on them, like a Half-Life 2 dark energy orb.
///     The system reflects the projectile's velocity before actual contact,
///     so collision handlers (damage, trigger explosions) never see the wall.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class MLRicochetComponent : Component
{
    /// <summary>
    ///     How many wall bounces the projectile can perform before it loses
    ///     the ability and behaves like a normal projectile (explodes on contact).
    /// </summary>
    [DataField, AutoNetworkedField]
    public int MaxBounces = 4;

    /// <summary>
    ///     Minimum raycast lookahead distance, in meters.
    /// </summary>
    [DataField]
    public float MinRange = 0.5f;

    /// <summary>
    ///     Despawn the projectile when it is over an empty tile / open map
    ///     (HL2 orbs vanish when they fly out under the open sky).
    /// </summary>
    [DataField]
    public bool DespawnInOpenSky = true;

    /// <summary>
    ///     Runtime bounce counter (not networked; only used for prediction).
    /// </summary>
    public int RemainingBounces;
}
