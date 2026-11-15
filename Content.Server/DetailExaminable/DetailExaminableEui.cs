using Content.Server.DetailExaminable;
using Content.Server.EUI;
using Content.Shared.DetailExaminable;
using Content.Shared.Eui;
using Content.Shared.Preferences;
using JetBrains.Annotations;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server.DetailExaminable
{
    [UsedImplicitly]
    public sealed class DetailExaminableEui : BaseEui
    {
        [Dependency] private readonly IEntityManager _entities = default!;
        private readonly EntityUid _target;
        private readonly DetailExaminableEditMode _mode;
        private readonly bool _append;
        private readonly string? _appendPrefix;
        private readonly string? _appendTitle;
        private readonly string? _appendPlaceholder;
        private readonly ForcedMarkingType? _forcedType;

        public DetailExaminableEui(EntityUid target, DetailExaminableEditMode mode = DetailExaminableEditMode.Content,
            bool append = false, string? appendPrefix = null, string? appendTitle = null, string? appendPlaceholder = null,
            ForcedMarkingType? forcedType = null)
        {
            _target = target;
            _mode = mode;
            _append = append;
            _appendPrefix = appendPrefix;
            _appendTitle = appendTitle;
            _appendPlaceholder = appendPlaceholder;
            _forcedType = forcedType;
            IoCManager.InjectDependencies(this);
        }

        public override EuiStateBase GetNewState()
        {
            var comp = _entities.GetComponent<DetailExaminableComponent>(_target);
            var currentText = _append
                ? string.Empty
                : _mode == DetailExaminableEditMode.Content
                    ? comp.Content
                    : comp.UniqueFeatures;

            var placeholder = _append
                ? _appendPlaceholder ?? Loc.GetString(_mode == DetailExaminableEditMode.Content
                    ? "detail-examinable-write-pen-placeholder"
                    : "detail-examinable-write-knife-placeholder")
                : string.Empty;

            var title = _append
                ? _appendTitle ?? Loc.GetString(_mode == DetailExaminableEditMode.Content
                    ? "detail-examinable-write-pen-title"
                    : "detail-examinable-write-knife-title")
                : Loc.GetString(_mode == DetailExaminableEditMode.Content
                    ? "detail-examinable-edit-content-title"
                    : "detail-examinable-edit-features-title");

            // While writing a marking, show what is already on the body.
            var existing = _append ? GetSystem().GetExistingMarkingsText(comp) : string.Empty;

            return new DetailExaminableEuiState(currentText, placeholder, title, _append, existing);
        }

        private DetailExaminableSystem GetSystem()
        {
            return IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<DetailExaminableSystem>();
        }

        public override void HandleMessage(EuiMessageBase msg)
        {
            if (msg is not SaveDetailExaminableMessage save)
            {
                base.HandleMessage(msg);
                return;
            }

            var text = save.Text.Trim();
            if (_append)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    // Writing/carving an empty message is not allowed: tell the player and
                    // keep the window open so they can type something.
                    if (Player.AttachedEntity is { } playerEnt)
                    {
                        var popup = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<Content.Server.Popups.PopupSystem>();
                        popup.PopupEntity(Loc.GetString("detail-examinable-write-empty-popup"), playerEnt, playerEnt);
                    }

                    return;
                }

                if (text.Length > DetailExaminableSystem.MaxMarkingLength)
                {
                    if (Player.AttachedEntity is { } playerEnt)
                    {
                        var popup = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<Content.Server.Popups.PopupSystem>();
                        popup.PopupEntity(Loc.GetString("detail-examinable-write-too-long-popup",
                            ("limit", DetailExaminableSystem.MaxMarkingLength)), playerEnt, playerEnt);
                    }

                    // Keep the window open so the player can shorten the text.
                    return;
                }

                if (_entities.TryGetComponent<DetailExaminableComponent>(_target, out var comp))
                {
                    // Forced writing (pen/knife) goes through a DoAfter: the player must stand
                    // still (progress bar + sound + chat text) before the marking is applied.
                    if (_forcedType is { } forcedType)
                    {
                        Close();

                        if (Player.AttachedEntity is { } user)
                        {
                            var system = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<DetailExaminableSystem>();
                            system.TryStartWriting(user, _target, forcedType, text);
                        }

                        return;
                    }
                }

                Close();
                return;
            }

            if (text.Length > HumanoidCharacterProfile.MaxDescLength)
            {
                Close();
                return;
            }

            if (_entities.TryGetComponent<DetailExaminableComponent>(_target, out var comp2))
            {
                if (_mode == DetailExaminableEditMode.Content)
                    comp2.Content = text;
                else
                    comp2.UniqueFeatures = text;
            }

            Close();
        }
    }
}