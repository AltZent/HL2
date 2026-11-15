using Content.Shared.DoAfter;
using Content.Shared.Eui;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared.DetailExaminable
{
    [Serializable, NetSerializable]
    public enum DetailExaminableEditMode : byte
    {
        Content,
        UniqueFeatures,
    }

    [Serializable, NetSerializable]
    public enum ForcedMarkingType : byte
    {
        Pen,
        Knife,
        Welder,
        Pierce,
        Burn,
        Blood,
        Paint,
    }

    /// <summary>
    ///     Raised when a DoAfter for writing on / carving into a body finishes.
    ///     On success the forced marking is applied to the target.
    /// </summary>
    [Serializable, NetSerializable]
    public sealed partial class DetailExaminableWriteDoAfterEvent : DoAfterEvent
    {
        [DataField]
        public ForcedMarkingType Type;

        [DataField]
        public string Text = string.Empty;

        private DetailExaminableWriteDoAfterEvent()
        {
        }

        public DetailExaminableWriteDoAfterEvent(ForcedMarkingType type, string text)
        {
            Type = type;
            Text = text;
        }

        public override DoAfterEvent Clone() => new DetailExaminableWriteDoAfterEvent(Type, Text);
    }

    [Serializable, NetSerializable]
    public sealed class DetailExaminableEuiState : EuiStateBase
    {
        public string CurrentText = string.Empty;
        public string Placeholder = string.Empty;
        public string WindowTitle = string.Empty;
        public bool Append;

        /// <summary>
        ///     Rendered list of markings already on the body (shown as a read-only
        ///     preview while writing a new one).
        /// </summary>
        public string ExistingMarkings = string.Empty;

        public DetailExaminableEuiState(string currentText, string placeholder, string windowTitle, bool append,
            string existingMarkings = "")
        {
            CurrentText = currentText;
            Placeholder = placeholder;
            WindowTitle = windowTitle;
            Append = append;
            ExistingMarkings = existingMarkings;
        }
    }

    [Serializable, NetSerializable]
    public sealed class SaveDetailExaminableMessage : EuiMessageBase
    {
        public string Text = string.Empty;
    }
}