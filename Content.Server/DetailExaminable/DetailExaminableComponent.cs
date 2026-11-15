using Content.Shared.DetailExaminable;

namespace Content.Server.DetailExaminable
{
    [RegisterComponent]
    public sealed partial class DetailExaminableComponent : Component
    {
        [DataField("content", required: true)] [ViewVariables(VVAccess.ReadWrite)]
        public string Content = "";

        [DataField("unique_features")] [ViewVariables(VVAccess.ReadWrite)]
        public string UniqueFeatures = "";

/// <summary>
    ///     In-character markings forced onto the body by a pen, knife, heat,
    ///     blood or paint. Pen/paint/blood markings wash off with water; knife
    ///     markings are not affected by water (only a bandage could cover them up later).
    /// </summary>
    [DataField("forced_markings")] [ViewVariables(VVAccess.ReadWrite)]
    public List<ForcedMarking> ForcedMarkings = new();
}

    [DataDefinition]
    public sealed partial class ForcedMarking
    {
        [DataField("type")] public ForcedMarkingType Type = ForcedMarkingType.Pen;

        [DataField("text")] public string Text = string.Empty;

        /// <summary>True once a knife/pierce marking has been bandaged (no longer an open wound).</summary>
        [DataField("bandaged")] public bool Bandaged;

        /// <summary>When (real server time) a temporary marking (blood/paint) dries out and disappears.</summary>
        [DataField("expires")] public TimeSpan? Expires;
    }
}