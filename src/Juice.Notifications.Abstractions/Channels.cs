namespace Juice.Notifications
{
    /// <summary>What a channel's addresses are, so an external address can be checked and normalized (design §9.1).</summary>
    public enum AddressKind
    {
        /// <summary>No address: shared targets only (a webhook URL lives on its integration target, never in a request).</summary>
        None,
        /// <summary>An email address, lowercased.</summary>
        Email,
        /// <summary>
        /// An account on a chat service (a Slack user, a Telegram chat), linked by the person through the service's bot (phase-4-plan I41): never
        /// entered by hand nor named in a request, and never matched by name or email (D23).
        /// </summary>
        Account,
    }

    /// <summary>Who a channel reaches.</summary>
    [Flags]
    public enum ChannelAudience
    {
        People = 1,
        SharedTargets = 2,
    }

    /// <summary>What a rendered template becomes on a delivery, which is what the channel's providers send (design §8.1).</summary>
    public enum ContentShape
    {
        /// <summary>A message with a subject, a text body and an HTML body (<c>EmailContent</c>); a first line <c>Subject: …</c> gives the subject.</summary>
        Message,
        /// <summary>A request body with its content type (<c>WebhookContent</c>): JSON when the output parses as JSON, else plain text.</summary>
        Body,
        /// <summary>
        /// An inbox item (<c>InboxContent</c>): a title, a text body and a link; a first line <c>Title: …</c> gives the title (otherwise the
        /// type's name), and the link is the request's <c>links.self</c>.
        /// </summary>
        Item,
        /// <summary>
        /// A chat message (<c>ChatContent</c>): a title, a text body and a link, as the inbox item's; and the type's actions, as buttons where the
        /// provider can take their answer. The text is plain: each provider escapes it for its own markup, so no data value can mention a whole
        /// channel or forge a link. With no template, the type's name.
        /// </summary>
        Chat,
    }

    /// <summary>
    /// What a channel is to the core (design D24, §9.1). A channel package registers one; types, templates, recipients and integrations are checked
    /// against the registered set, and the delivery planner and dispatcher act on its fields, never on its key. Phase 1 registers <c>webhook</c>
    /// and <c>email</c>. Per-channel content limits and escaping come with the channels that need them (phase 2: SMS, chat).
    /// </summary>
    /// <param name="Key">Lowercase, unique: <c>email</c>, <c>webhook</c>.</param>
    /// <param name="Address">The address kind of an external recipient on this channel; <see cref="AddressKind.None"/> allows none.</param>
    /// <param name="Audience">Whether it reaches people, shared targets, or both.</param>
    /// <param name="Content">What a rendered template becomes.</param>
    /// <param name="Passthrough">
    /// The channel may send the producer's data as is, with no template (<c>"passthrough"</c>): the request's envelope, as a JSON
    /// <see cref="ContentShape.Body"/>. A type with no template for such a channel is sent passthrough.
    /// </param>
    /// <param name="RepeatsSafely">
    /// A send whose outcome is unknown may be repeated (§10.3): receivers deduplicate on an id that is the same on every attempt (webhook's
    /// <c>webhook-id</c>). Otherwise only an idempotent provider, or a mandatory type once, repeats one.
    /// </param>
    public sealed record ChannelDefinition(string Key, AddressKind Address, ChannelAudience Audience, ContentShape Content,
        bool Passthrough = false, bool RepeatsSafely = false);

    /// <summary>The registered channels.</summary>
    public interface IChannelRegistry
    {
        ChannelDefinition? Find(string key);

        IReadOnlyCollection<ChannelDefinition> All { get; }
    }

    /// <summary>
    /// The registry over every <see cref="ChannelDefinition"/> registered in the container, and the built-in channels whose keys none of them has
    /// (when <see cref="BuiltInChannelFallback"/> is registered): a provider package's own definition of <c>webhook</c> or <c>email</c> takes the
    /// built-in's place, however and whenever it is registered. Two registered definitions with one key are refused, and so is a passthrough
    /// channel whose content isn't a <see cref="ContentShape.Body"/>.
    /// </summary>
    public sealed class ChannelRegistry : IChannelRegistry
    {
        private readonly Dictionary<string, ChannelDefinition> _channels;

        public ChannelRegistry(IEnumerable<ChannelDefinition> definitions, BuiltInChannelFallback? builtIns = null)
        {
            // The inbox is the core's own (design §9.1): always there, and no package defines it.
            _channels = new Dictionary<string, ChannelDefinition>(StringComparer.Ordinal) { [Channels.Inbox] = BuiltInChannels.Inbox };
            foreach (var definition in definitions)
            {
                if (definition.Key == Channels.Inbox)
                {
                    throw new InvalidOperationException($"The channel '{Channels.Inbox}' is the service's own; a package can't define it.");
                }
                if (definition.Passthrough && definition.Content != ContentShape.Body)
                {
                    // Passthrough sends the request's envelope as a JSON body; a channel whose content is a message has nothing to put it in.
                    throw new InvalidOperationException($"The channel '{definition.Key}' is passthrough, so its content is a {nameof(ContentShape.Body)}.");
                }
                if (!_channels.TryAdd(definition.Key, definition))
                {
                    throw new InvalidOperationException($"The channel '{definition.Key}' is registered twice.");
                }
            }
            if (builtIns is not null)
            {
                foreach (var builtIn in BuiltInChannels.All)
                {
                    _channels.TryAdd(builtIn.Key, builtIn);
                }
            }
        }

        public ChannelDefinition? Find(string key) => _channels.GetValueOrDefault(key);

        public IReadOnlyCollection<ChannelDefinition> All => _channels.Values;
    }

    /// <summary>Registered, it has <see cref="ChannelRegistry"/> add the built-in channels whose keys no registered definition has.</summary>
    public sealed class BuiltInChannelFallback
    {
        public static readonly BuiltInChannelFallback Instance = new();

        private BuiltInChannelFallback()
        {
        }
    }

    /// <summary>The built-in definitions: phase 1's webhook and email (replaceable by a package), and the inbox (always the core's).</summary>
    public static class BuiltInChannels
    {
        /// <summary>
        /// The in-app inbox (phase-2-plan I17): reaches people only, at no address (the person is the destination, <c>inbox:{subscriberId}</c>),
        /// through the service's own provider, which writes one item per delivery and so repeats safely.
        /// </summary>
        public static readonly ChannelDefinition Inbox = new(Channels.Inbox, AddressKind.None, ChannelAudience.People, ContentShape.Item, RepeatsSafely: true);

        public static readonly ChannelDefinition Webhook = new(Channels.Webhook, AddressKind.None, ChannelAudience.SharedTargets, ContentShape.Body,
            Passthrough: true, RepeatsSafely: true);
        public static readonly ChannelDefinition Email = new(Channels.Email, AddressKind.Email, ChannelAudience.People | ChannelAudience.SharedTargets,
            ContentShape.Message);

        public static IReadOnlyList<ChannelDefinition> All { get; } = [Webhook, Email];
    }
}
