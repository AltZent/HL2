using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Server.Chat.Systems;
using Content.Server.EUI;
using Content.Server.Popups;
using Content.Server.Stack;
using Content.Server.Verbs;
using Content.Shared.Chat;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.DetailExaminable;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Paint;
using Content.Shared.Tag;
using Content.Shared.Temperature;
using Content.Shared.Tools.Components;
using Content.Shared.Verbs;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Content.Server.DetailExaminable
{
    public sealed class DetailExaminableSystem : EntitySystem
    {
        [Dependency] private readonly ExamineSystemShared _examineSystem = default!;
        [Dependency] private readonly VerbSystem _verbSystem = default!;
        [Dependency] private readonly SharedHandsSystem _hands = default!;
        [Dependency] private readonly TagSystem _tags = default!;
        [Dependency] private readonly SharedTransformSystem _transform = default!;
        [Dependency] private readonly SharedSolutionContainerSystem _solutionContainer = default!;
        [Dependency] private readonly OpenableSystem _openable = default!;
        [Dependency] private readonly PopupSystem _popup = default!;
        [Dependency] private readonly ChatSystem _chat = default!;
        [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
        [Dependency] private readonly SharedAudioSystem _audio = default!;
        [Dependency] private readonly StackSystem _stacks = default!;
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly BloodstreamSystem _bloodstream = default!;

        /// <summary>Arm's reach — writing on someone with a pen requires you to be right next to them.</summary>
        private const float InteractionRange = 1.1f;

        /// <summary>Maximum number of forced markings a single body can carry.</summary>
        private const int MaxMarkings = 20;

        /// <summary>Maximum length of a single marking text.</summary>
        public const int MaxMarkingLength = 250;

        /// <summary>Extra bleed rate added when writing with blood (tearing the wound open).</summary>
        private const float BloodWriteBleedIncrease = 0.5f;

        /// <summary>How long writing with a pen on a body takes (seconds).</summary>
        private const float PenWriteDelay = 3f;

        /// <summary>How long carving a knife marking takes (seconds).</summary>
        private const float KnifeWriteDelay = 5f;

        /// <summary>How long burning a marking with a lit welder takes (seconds).</summary>
        private const float WelderWriteDelay = 6f;

        /// <summary>How long leaving a punctured mark with a screwdriver/needle takes (seconds).</summary>
        private const float PierceWriteDelay = 4f;

        /// <summary>How long searing a mark with a glowing-hot item takes (seconds).</summary>
        private const float BurnWriteDelay = 5f;

        /// <summary>How long writing a mark with your own blood takes (seconds).</summary>
        private const float BloodWriteDelay = 2f;

        /// <summary>How long spraying a paint mark takes (seconds).</summary>
        private const float PaintWriteDelay = 4f;

        /// <summary>How long a temporary blood/paint marking stays on the body before drying out.</summary>
        private static readonly TimeSpan MarkingLifetime = TimeSpan.FromMinutes(60);

        private static readonly FixedPoint2 WashAmount = FixedPoint2.New(5);

        /// <summary>How often expirable (blood/paint) markings are checked for drying out.</summary>
        private static readonly TimeSpan ExpiryCheckInterval = TimeSpan.FromSeconds(2);

        /// <summary>When to next prune dried-out markings.</summary>
        private TimeSpan _nextExpiryCheck;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<DetailExaminableComponent, GetVerbsEvent<ExamineVerb>>(OnGetExamineVerbs);
            SubscribeLocalEvent<DetailExaminableComponent, GetVerbsEvent<Verb>>(OnGetWriteVerbs);
            SubscribeLocalEvent<DetailExaminableComponent, InteractUsingEvent>(OnInteractUsing);
            SubscribeLocalEvent<DetailExaminableComponent, DetailExaminableWriteDoAfterEvent>(OnWriteDoAfter);
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            if (_timing.CurTime < _nextExpiryCheck)
                return;
            _nextExpiryCheck = _timing.CurTime + ExpiryCheckInterval;

            var curTime = _timing.CurTime;
            var query = EntityManager.EntityQueryEnumerator<DetailExaminableComponent>();
            while (query.MoveNext(out _, out var comp))
            {
                // Temporary markings (blood/paint) dry out and actually disappear.
                comp.ForcedMarkings.RemoveAll(m => m.Expires is { } expires && expires <= curTime);
            }
        }

        private void OnGetWriteVerbs(EntityUid uid, DetailExaminableComponent component, GetVerbsEvent<Verb> args)
        {
            // Writing/carving on someone (including yourself) requires a tool (pen, crayon, knife...)
            // in your active hand.
            if (_hands.TryGetActiveItem(args.User, out var held) && held.HasValue)
            {
                // The message forced before the text depends on what was used to make the mark.
                if (_tags.HasTag(held.Value, "Write"))
                {
                    args.Verbs.Add(GetWriteVerb(args, ForcedMarkingType.Pen,
                        Loc.GetString("detail-examinable-write-pen-verb"),
                        Loc.GetString("detail-examinable-write-pen-title"),
                        Loc.GetString("detail-examinable-write-pen-placeholder")));
                    return;
                }

                if (_tags.HasTag(held.Value, "Knife"))
                {
                    args.Verbs.Add(GetWriteVerb(args, ForcedMarkingType.Knife,
                        Loc.GetString("detail-examinable-write-knife-verb"),
                        Loc.GetString("detail-examinable-write-knife-title"),
                        Loc.GetString("detail-examinable-write-knife-placeholder")));
                    return;
                }

                // A pointed tool (screwdriver, needle...) pokes small, neat marks into the skin.
                if (_tags.HasTag(held.Value, "Screwdriver") || _tags.HasTag(held.Value, "Needle"))
                {
                    args.Verbs.Add(GetWriteVerb(args, ForcedMarkingType.Pierce,
                        Loc.GetString("detail-examinable-write-pierce-verb"),
                        Loc.GetString("detail-examinable-write-pierce-title"),
                        Loc.GetString("detail-examinable-write-pierce-placeholder")));
                    return;
                }

                // A can of spray paint writes a temporary, washable mark.
                if (TryComp<PaintComponent>(held.Value, out _))
                {
                    args.Verbs.Add(GetWriteVerb(args, ForcedMarkingType.Paint,
                        Loc.GetString("detail-examinable-write-paint-verb"),
                        Loc.GetString("detail-examinable-write-paint-title"),
                        Loc.GetString("detail-examinable-write-paint-placeholder")));
                    return;
                }

                // Burning a mark into the skin requires a lit welder (or similar torch tool).
                if (TryComp<WelderComponent>(held.Value, out var welder) && welder.Enabled)
                {
                    args.Verbs.Add(GetWriteVerb(args, ForcedMarkingType.Welder,
                        Loc.GetString("detail-examinable-write-welder-verb"),
                        Loc.GetString("detail-examinable-write-welder-title"),
                        Loc.GetString("detail-examinable-write-welder-placeholder")));
                    return;
                }

                // Any other glowing-hot item (lit match, cigarette, flare...) sears a burn mark.
                var hotEvent = new IsHotEvent();
                RaiseLocalEvent(held.Value, hotEvent);
                if (hotEvent.IsHot)
                {
                    args.Verbs.Add(GetWriteVerb(args, ForcedMarkingType.Burn,
                        Loc.GetString("detail-examinable-write-burn-verb"),
                        Loc.GetString("detail-examinable-write-burn-title"),
                        Loc.GetString("detail-examinable-write-burn-placeholder")));
                    return;
                }
            }

            // Writing with blood requires no tool — just a bleeding wound on either actor.
            if (IsBleeding(args.User) || IsBleeding(args.Target))
            {
                args.Verbs.Add(GetWriteVerb(args, ForcedMarkingType.Blood,
                    Loc.GetString("detail-examinable-write-blood-verb"),
                    Loc.GetString("detail-examinable-write-blood-title"),
                    Loc.GetString("detail-examinable-write-blood-placeholder")));
            }
        }

        private bool IsBleeding(EntityUid uid)
        {
            return TryComp<BloodstreamComponent>(uid, out var bloodstream)
                && bloodstream.BleedAmount > 0f;
        }

        /// <summary>
        ///     Washing a bottle of water over someone washes off pen markings.
        ///     Knife markings are not affected by water — they can only be handled with
        ///     a bandage. The bottle must be open and contain water.
        /// </summary>
        private void OnInteractUsing(Entity<DetailExaminableComponent> entity, ref InteractUsingEvent args)
        {
            if (args.Handled || args.Used == EntityUid.Invalid)
                return;

            // Bandaging: a gauze/bandage wraps up unbandaged knife markings.
            if (_tags.HasTag(args.Used, "Gauze"))
            {
                TryBandageKnifeMarks(entity, args);
                if (args.Handled)
                    return;
            }

            // Only pen/paint/blood markings can be washed off with water.
            if (!entity.Comp.ForcedMarkings.Any(m => m.Type is ForcedMarkingType.Pen or ForcedMarkingType.Paint or ForcedMarkingType.Blood))
                return;

            // Only water (from an open container) can wash the marks off.
            if (!TryFindWater(args.Used, out var soln, out _))
                return;

            if (_openable.IsClosed(args.Used, args.User))
                return;

            // Consume the water, then remove the pen-markings. Knife markings are left untouched.
            _solutionContainer.RemoveReagent(soln, "Water", WashAmount);
            entity.Comp.ForcedMarkings.RemoveAll(m => m.Type is ForcedMarkingType.Pen or ForcedMarkingType.Paint or ForcedMarkingType.Blood);

            args.Handled = true;

            // Feedback: a chat emote + a popup for everyone nearby.
            var target = Identity.Name(args.Target, EntityManager);
            _chat.TrySendInGameICMessage(args.User,
                Loc.GetString("detail-examinable-wash-chat", ("target", target)),
                InGameICChatType.Emote, ChatTransmitRange.Normal);
            _popup.PopupEntity(Loc.GetString("detail-examinable-wash-popup", ("target", target)), args.Target, args.User);
        }

        /// <summary>
        ///     Uses one gauze/bandage to wrap up all currently unbandaged knife markings
        ///     on the body. Knife markings cannot be washed off with water and stay on
        ///     the person's features until they are bandaged.
        /// </summary>
        private void TryBandageKnifeMarks(Entity<DetailExaminableComponent> entity, InteractUsingEvent args)
        {
            var unbandaged = entity.Comp.ForcedMarkings.Any(m => (m.Type is ForcedMarkingType.Knife or ForcedMarkingType.Pierce) && !m.Bandaged);
            if (!unbandaged)
                return;

            // Consume one bandage from the stack (gauze).
            _stacks.Use(args.Used, 1);

            foreach (var mark in entity.Comp.ForcedMarkings)
            {
                if (mark.Type is ForcedMarkingType.Knife or ForcedMarkingType.Pierce)
                    mark.Bandaged = true;
            }

            args.Handled = true;

            // Sound signal: wrapping a wound with gauze.
            _audio.PlayPvs("/Audio/Items/Medical/brutepack_end.ogg", entity.Owner);

            // Text feedback for everyone nearby + a popup for the user.
            var target = Identity.Name(entity.Owner, EntityManager);
            _chat.TrySendInGameICMessage(args.User,
                Loc.GetString("detail-examinable-bandage-chat", ("target", target)),
                InGameICChatType.Emote, ChatTransmitRange.Normal);
            _popup.PopupEntity(Loc.GetString("detail-examinable-bandage-popup", ("target", target)), entity.Owner, args.User);
        }

        /// <summary>
        ///     Finds the first solution on <paramref name="used"/> that actually contains water.
        /// </summary>
        private bool TryFindWater(EntityUid used, out Entity<SolutionComponent> soln, out Solution? solution)
        {
            soln = default;
            solution = null;

            if (!TryComp<SolutionContainerManagerComponent>(used, out var manager))
                return false;

            foreach (var (_, candidate) in _solutionContainer.EnumerateSolutions((used, manager)))
            {
                if (candidate.Comp.Solution.GetTotalPrototypeQuantity("Water") > FixedPoint2.Zero)
                {
                    soln = candidate;
                    solution = candidate.Comp.Solution;
                    return true;
                }
            }

            return false;
        }

        private Verb GetWriteVerb(GetVerbsEvent<Verb> args, ForcedMarkingType forcedType, string text, string title, string placeholder)
        {
            var inRange = Vector2.Distance(_transform.GetWorldPosition(args.User), _transform.GetWorldPosition(args.Target)) <= InteractionRange;

            return new Verb
            {
                Text = text,
                Category = VerbCategory.Interaction,
                Priority = 1,
                Disabled = !inRange,
                Message = inRange ? null : Loc.GetString("interaction-verb-cannot-reach"),
                Act = () =>
                {
                    if (!TryComp<ActorComponent>(args.User, out var actor))
                        return;

                    var eui = IoCManager.Resolve<EuiManager>();
                    eui.OpenEui(new DetailExaminableEui(args.Target, DetailExaminableEditMode.UniqueFeatures,
                        append: true, appendTitle: title, appendPlaceholder: placeholder, forcedType: forcedType),
                        actor.PlayerSession);
                }
            };
        }

        /// <summary>
        ///     Starts the DoAfter for writing on or carving into someone.
        ///     The marking is only applied when the DoAfter successfully completes.
        /// </summary>
        public bool TryStartWriting(EntityUid user, EntityUid target, ForcedMarkingType type, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !TryComp<DetailExaminableComponent>(target, out _))
                return false;

            if (text.Length > MaxMarkingLength)
            {
                _popup.PopupEntity(Loc.GetString("detail-examinable-write-too-long-popup",
                    ("limit", MaxMarkingLength)), user, user);
                return false;
            }

            var delay = type switch
            {
                ForcedMarkingType.Pen => PenWriteDelay,
                ForcedMarkingType.Knife => KnifeWriteDelay,
                ForcedMarkingType.Welder => WelderWriteDelay,
                ForcedMarkingType.Pierce => PierceWriteDelay,
                ForcedMarkingType.Burn => BurnWriteDelay,
                ForcedMarkingType.Blood => BloodWriteDelay,
                ForcedMarkingType.Paint => PaintWriteDelay,
                _ => PenWriteDelay,
            };
            var doAfterArgs = new DoAfterArgs(
                EntityManager,
                user,
                delay,
                new DetailExaminableWriteDoAfterEvent(type, text),
                eventTarget: target,
                target: target)
            {
                NeedHand = type != ForcedMarkingType.Blood,
                BreakOnMove = true, // this fork: breaks when the user OR the target moves
                BreakOnDamage = true,
                DamageThreshold = 1,
                DistanceThreshold = InteractionRange,
            };

            if (!_doAfter.TryStartDoAfter(doAfterArgs))
                return false;

            // The victim feels someone starting to mark their body and can break free by moving.
            if (target != user)
                _popup.PopupEntity(Loc.GetString("detail-examinable-write-target-warn",
                    ("writer", Identity.Name(user, EntityManager))), user, target);

            return true;
        }

        private void OnWriteDoAfter(Entity<DetailExaminableComponent> entity, ref DetailExaminableWriteDoAfterEvent args)
        {
            if (args.Cancelled || args.Handled || args.User == EntityUid.Invalid)
                return;

            args.Handled = true;

            // Server-authoritative guards: length, body capacity and duplicate markings.
            if (args.Text.Length > MaxMarkingLength)
            {
                _popup.PopupEntity(Loc.GetString("detail-examinable-write-too-long-popup",
                    ("limit", MaxMarkingLength)), args.User, args.User);
                return;
            }

            if (entity.Comp.ForcedMarkings.Count >= MaxMarkings)
            {
                _popup.PopupEntity(Loc.GetString("detail-examinable-write-full-popup",
                    ("target", Identity.Name(entity.Owner, EntityManager))), args.User, args.User);
                return;
            }

            var markText = args.Text;
            var markType = args.Type;
            if (entity.Comp.ForcedMarkings.Any(m =>
                    m.Type == markType && string.Equals(m.Text, markText, StringComparison.OrdinalIgnoreCase)))
            {
                _popup.PopupEntity(Loc.GetString("detail-examinable-write-duplicate-popup",
                    ("target", Identity.Name(entity.Owner, EntityManager))), args.User, args.User);
                return;
            }

            var hasHeld = _hands.TryGetActiveItem(args.User, out var held) && held.HasValue;

            // Writing with blood needs no tool — only an ongoing bleed on either actor.
            if (args.Type == ForcedMarkingType.Blood)
            {
                if (!IsBleeding(args.User) && !IsBleeding(entity.Owner))
                    return;

                // Writing with blood tears the wound open further.
                var bleeding = IsBleeding(args.User) ? args.User : entity.Owner;
                if (TryComp<BloodstreamComponent>(bleeding, out _))
                    _bloodstream.TryModifyBleedAmount(bleeding, BloodWriteBleedIncrease);
            }
            else if (!hasHeld)
            {
                return;
            }
            else if (args.Type == ForcedMarkingType.Welder)
            {
                if (!TryComp<WelderComponent>(held!.Value, out var welder) || !welder.Enabled)
                    return;
            }
            else if (args.Type == ForcedMarkingType.Pierce)
            {
                if (!_tags.HasTag(held!.Value, "Screwdriver") && !_tags.HasTag(held.Value, "Needle"))
                    return;
            }
            else if (args.Type == ForcedMarkingType.Paint)
            {
                if (!TryComp<PaintComponent>(held!.Value, out _))
                    return;
            }
            else if (args.Type == ForcedMarkingType.Burn)
            {
                var hotEvent = new IsHotEvent();
                RaiseLocalEvent(held!.Value, hotEvent);
                if (!hotEvent.IsHot)
                    return;
            }
            else if (args.Type == ForcedMarkingType.Knife && !_tags.HasTag(held!.Value, "Knife"))
            {
                return;
            }
            else if (args.Type == ForcedMarkingType.Pen && !_tags.HasTag(held!.Value, "Write"))
            {
                return;
            }

            entity.Comp.ForcedMarkings.Add(new ForcedMarking
            {
                Type = args.Type,
                Text = args.Text,
                // Blood and paint are temporary: they dry out and vanish later.
                Expires = args.Type is ForcedMarkingType.Blood or ForcedMarkingType.Paint
                    ? _timing.CurTime + MarkingLifetime
                    : null,
            });

            // Sound signal: scribbling with a pen / a blade slicing skin / a welder hissing.
            var sound = args.Type switch
            {
                ForcedMarkingType.Pen => "/Audio/Items/Paper/paper_scribble1.ogg",
                ForcedMarkingType.Knife => "/Audio/Weapons/bladeslice.ogg",
                ForcedMarkingType.Welder => "/Audio/Items/welder2.ogg",
                ForcedMarkingType.Pierce => "/Audio/Weapons/bladeslice.ogg",
                ForcedMarkingType.Burn => "/Audio/Items/welder2.ogg",
                ForcedMarkingType.Blood => "/Audio/Items/Paper/paper_scribble1.ogg",
                ForcedMarkingType.Paint => "/Audio/Effects/spray2.ogg",
                _ => "/Audio/Items/Paper/paper_scribble1.ogg",
            };
            _audio.PlayPvs(sound, entity.Owner);

            // Text feedback for everyone nearby + a popup for the writer.
            var target = Identity.Name(entity.Owner, EntityManager);
            var key = args.Type switch
            {
                ForcedMarkingType.Pen => "detail-examinable-write-pen-chat",
                ForcedMarkingType.Knife => "detail-examinable-write-knife-chat",
                ForcedMarkingType.Welder => "detail-examinable-write-welder-chat",
                ForcedMarkingType.Pierce => "detail-examinable-write-pierce-chat",
                ForcedMarkingType.Burn => "detail-examinable-write-burn-chat",
                ForcedMarkingType.Blood => "detail-examinable-write-blood-chat",
                ForcedMarkingType.Paint => "detail-examinable-write-paint-chat",
                _ => "detail-examinable-write-pen-chat",
            };
            _chat.TrySendInGameICMessage(args.User,
                Loc.GetString(key, ("target", target), ("text", args.Text)),
                InGameICChatType.Emote, ChatTransmitRange.Normal);
            _popup.PopupEntity(Loc.GetString(key, ("target", target), ("text", args.Text)), entity.Owner, args.User);
        }

        /// <summary>
        ///     Rendered loc prefix for a marking type (used in examine text and in the
        ///     writing window preview).
        /// </summary>
        public string GetMarkingPrefix(ForcedMarkingType type, bool bandaged)
        {
            return type switch
            {
                ForcedMarkingType.Pen => Loc.GetString("detail-examinable-write-pen-prefix"),
                ForcedMarkingType.Knife => bandaged
                    ? Loc.GetString("detail-examinable-write-knife-bandaged")
                    : Loc.GetString("detail-examinable-write-knife-prefix"),
                ForcedMarkingType.Pierce => bandaged
                    ? Loc.GetString("detail-examinable-write-pierce-bandaged")
                    : Loc.GetString("detail-examinable-write-pierce-prefix"),
                ForcedMarkingType.Welder => Loc.GetString("detail-examinable-write-welder-prefix"),
                ForcedMarkingType.Burn => Loc.GetString("detail-examinable-write-burn-prefix"),
                ForcedMarkingType.Blood => Loc.GetString("detail-examinable-write-blood-prefix"),
                ForcedMarkingType.Paint => Loc.GetString("detail-examinable-write-paint-prefix"),
                _ => string.Empty,
            };
        }

        /// <summary>
        ///     Builds the read-only preview of markings already on the body.
        /// </summary>
        public string GetExistingMarkingsText(DetailExaminableComponent component)
        {
            if (component.ForcedMarkings.Count == 0)
                return string.Empty;

            return string.Join("\n", component.ForcedMarkings.Select(m =>
                GetMarkingPrefix(m.Type, m.Bandaged) + " " + FormattedMessage.EscapeText(m.Text)));
        }

        private static readonly (string Root, string Color)[] FeatureKeywordColors =
        {
            // Written marks made by a pen / written text.
            ("татуировк", "#9e9e9e"),
            ("написан", "#9e9e9e"),
            // Marks carved with a blade / battle wounds.
            ("вырезан", "#a00000"),
            ("шрам", "#a00000"),
            ("рана", "#a00000"),
            ("порез", "#a00000"),
            // Burns get a warm orange tint.
            ("ожог", "#ff8c00"),
            ("выжжен", "#ff8c00"),
            // Implants get a cool blue tint.
            ("имплант", "#5da9e8"),
        };

        /// <summary>
        ///     Renders the stored "special features" markup, auto-coloring known keywords
        ///     (burns, scars, implants, written/carved marks...) unless the text already
        ///     specifies its own color. Text the player wrapped in their own color tags is kept untouched.
        /// </summary>
        private static string AutoColorFeatures(string features)
        {
            if (!FormattedMessage.TryFromMarkup(features, out var parsed))
                parsed = FormattedMessage.FromMarkupPermissive(features, out _);

            var builder = new StringBuilder();
            // Tracks how many user color tags are open, so we don't fight their chosen color.
            var colorDepth = 0;
            var pattern = $@"\b(?<kw>{string.Join("|", FeatureKeywordColors.Select(f => f.Root))})\w*\b";

            foreach (var node in parsed.Nodes)
            {
                if (node.Name == "color")
                {
                    if (node.Closing)
                        colorDepth = Math.Max(0, colorDepth - 1);
                    else
                        colorDepth++;

                    builder.Append(node);
                    continue;
                }

                if (node.Name != null)
                {
                    builder.Append(node);
                    continue;
                }

                var text = node.Value.StringValue;
                if (text == null)
                    continue;

                builder.Append(colorDepth > 0
                    ? FormattedMessage.EscapeText(text)
                    : Regex.Replace(FormattedMessage.EscapeText(text), pattern, static m =>
                    {
                        var root = FeatureKeywordColors.First(f => m.Value.StartsWith(f.Root, StringComparison.OrdinalIgnoreCase)).Root;
                        var color = FeatureKeywordColors.First(f => f.Root == root).Color;
                        return $"[color={color}]{m.Value}[/color]";
                    }, RegexOptions.IgnoreCase));
            }

            return builder.ToString();
        }

        private void OnGetExamineVerbs(EntityUid uid, DetailExaminableComponent component, GetVerbsEvent<ExamineVerb> args)
        {
            if (Identity.Name(args.Target, EntityManager) != MetaData(args.Target).EntityName)
                return;

            var detailsRange = _examineSystem.IsInDetailsRange(args.User, uid);

            var verb = new ExamineVerb()
            {
                Act = () =>
                {
                    var markup = new FormattedMessage();
                    markup.AddMarkup(component.Content);

                    var features = component.UniqueFeatures;
                    // Forced markings (pen / knife) are always appended after the ordinary features.
                    if (component.ForcedMarkings.Count > 0)
                    {
                        var lines = features;
                        if (!string.IsNullOrEmpty(lines))
                            lines += "\n";

                        lines += string.Join("\n", component.ForcedMarkings.Select(m =>
                            m.Type switch
                            {
                                ForcedMarkingType.Pen => Loc.GetString("detail-examinable-write-pen-prefix") + " " + FormattedMessage.EscapeText(m.Text),
                                ForcedMarkingType.Knife => m.Bandaged
                                    ? Loc.GetString("detail-examinable-write-knife-bandaged")
                                    : Loc.GetString("detail-examinable-write-knife-prefix") + " " + FormattedMessage.EscapeText(m.Text),
                                ForcedMarkingType.Pierce => m.Bandaged
                                    ? Loc.GetString("detail-examinable-write-pierce-bandaged")
                                    : Loc.GetString("detail-examinable-write-pierce-prefix") + " " + FormattedMessage.EscapeText(m.Text),
                                ForcedMarkingType.Welder => Loc.GetString("detail-examinable-write-welder-prefix") + " " + FormattedMessage.EscapeText(m.Text),
                                ForcedMarkingType.Burn => Loc.GetString("detail-examinable-write-burn-prefix") + " " + FormattedMessage.EscapeText(m.Text),
                                ForcedMarkingType.Blood => Loc.GetString("detail-examinable-write-blood-prefix") + " " + FormattedMessage.EscapeText(m.Text),
                                ForcedMarkingType.Paint => Loc.GetString("detail-examinable-write-paint-prefix") + " " + FormattedMessage.EscapeText(m.Text),
                                _ => FormattedMessage.EscapeText(m.Text),
                            }));
                        features = lines;
                    }

                    if (!string.IsNullOrWhiteSpace(features))
                    {
                        markup.PushNewline();
                        markup.AddMarkup(Loc.GetString("detail-examinable-unique-header", ("features", AutoColorFeatures(features))));
                    }

                    if (args.User != args.Target)
                    {
                        _examineSystem.SendExamineTooltip(args.User, uid, markup, false, false);
                        return;
                    }

                    if (!TryComp<ActorComponent>(args.User, out var actor))
                        return;

                    var verbs = _verbSystem.GetLocalVerbs(uid, args.User, typeof(ExamineVerb));
                    foreach (var v in verbs.Where(v => v.Text == Loc.GetString("detail-examinable-verb-text")).ToList())
                    {
                        verbs.Remove(v);
                    }

                    // The edit verb is added in OnGetExamineVerbs above (hidden from the normal tooltip),
                    // so it can be resolved by the server via GetLocalVerbs when the button is pressed.
                    // Flip it to visible for this tooltip only.
                    foreach (var v in verbs.Where(v => v.Text == Loc.GetString("detail-examinable-edit-verb-text")).ToList())
                    {
                        if (v is ExamineVerb examine)
                            examine.ShowOnExamineTooltip = true;
                    }

                    var message = new ExamineSystemMessages.ExamineInfoResponseMessage(
                        GetNetEntity(uid), 0, markup, verbs.ToList(), centerAtCursor: false);
                    RaiseNetworkEvent(message, actor.PlayerSession.Channel);
                },
                Text = Loc.GetString("detail-examinable-verb-text"),
                Category = VerbCategory.Examine,
                Priority = 10,
                Disabled = !detailsRange,
                Message = detailsRange ? null : Loc.GetString("detail-examinable-verb-disabled"),
                Icon = new SpriteSpecifier.Texture(new ("/Textures/Interface/VerbIcons/examine.svg.192dpi.png"))
            };

            args.Verbs.Add(verb);

            // Only the player themselves can edit their own description. This verb has to exist in the
            // server-side verb list so that when the client sends ExecuteVerbEvent for the "Изменить описание"
            // button from the "Подробности" tooltip, the server can find it via GetLocalVerbs + TryGetValue.
            // It is hidden from the normal examine tooltip and only shown inside the "Подробности" tooltip.
            if (args.User == args.Target)
            {
                args.Verbs.Add(new ExamineVerb()
                {
                    Act = () =>
                    {
                        if (!TryComp<ActorComponent>(args.User, out var actor))
                            return;

                        var eui = IoCManager.Resolve<EuiManager>();
                        eui.OpenEui(new DetailExaminableEui(uid), actor.PlayerSession);
                    },
                    Text = Loc.GetString("detail-examinable-edit-verb-text"),
                    Category = VerbCategory.Examine,
                    Priority = 5,
                    Icon = new SpriteSpecifier.Texture(new ("/Textures/Interface/VerbIcons/edit.svg.png")),
                    ShowOnExamineTooltip = false,
                    CloseMenu = true
                });
            }
        }
    }
}