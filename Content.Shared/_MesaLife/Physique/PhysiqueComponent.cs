using Robust.Shared.GameStates;

namespace Content.Shared._MesaLife.Physique;

/// <summary>
///     Stores a character's actual physique (height in cm, weight in kg) and the
///     resulting stat-modifier axes. Filled in by <see cref="PhysiqueSystem"/> when
///     the humanoid's scale is set from the lobby profile. Default values produce
///     no effects at all.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PhysiqueComponent : Component
{
    /// <summary>Height of this character in centimeters.</summary>
    [AutoNetworkedField, DataField]
    public float HeightCm;

    /// <summary>Average height for this character's species, in centimeters.</summary>
    [AutoNetworkedField, DataField]
    public float AvgHeightCm;

    /// <summary>Weight of this character in kilograms.</summary>
    [AutoNetworkedField, DataField]
    public float WeightKg;

    /// <summary>Average weight for this character's species, in kilograms.</summary>
    [AutoNetworkedField, DataField]
    public float AvgWeightKg;

    /// <summary>Height axis: -2 very short .. 0 neutral .. +2 very tall.</summary>
    [AutoNetworkedField, DataField]
    public sbyte HeightAxis;

    /// <summary>Weight axis: -2 very light .. 0 neutral .. +2 very heavy.</summary>
    [AutoNetworkedField, DataField]
    public sbyte WeightAxis;
}
