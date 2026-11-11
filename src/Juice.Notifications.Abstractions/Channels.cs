namespace Juice.Notifications
{
    /// <summary>What a channel's addresses are, so an external address can be checked and normalized (design §9.1).</summary>
    public enum AddressKind
    {
        /// <summary>No address: shared targets only (a webhook URL lives on its integration target, never in a request).</summary>
        None,
        /// <summary>An email address, lowercased.</summary>
        Email,
    }

    /// <summary>Who a channel reaches.</summary>
    [Flags]
    public enum ChannelAudience
    {
        People = 1,
        SharedTargets = 2,
    }

    /// <summary>
    /// What a channel is to the core (design D24, §9.1). A channel package registers one; types, templates, recipients and integrations are checked
    /// against the registered set. Phase 1 registers <c>webhook</c> and <c>email</c>. The render shape, limits, escaping and unknown-outcome policy
    /// are added with the providers (phase-1-plan M6).
    /// </summary>
    /// <param name="Key">Lowercase, unique: <c>email</c>, <c>webhook</c>.</param>
    /// <param name="Address">The address kind of an external recipient on this channel; <see cref="AddressKind.None"/> allows none.</param>
    /// <param name="Audience">Whether it reaches people, shared targets, or both.</param>
    /// <param name="Passthrough">The channel may send the producer's data as is, with no template (<c>"passthrough"</c>).</param>
    public sealed record ChannelDefinition(string Key, AddressKind Address, ChannelAudience Audience, bool Passthrough = false);

    /// <summary>The registered channels.</summary>
    public interface IChannelRegistry
    {
        ChannelDefinition? Find(string key);

        IReadOnlyCollection<ChannelDefinition> All { get; }
    }

    /// <summary>The registry over every <see cref="ChannelDefinition"/> registered in the container. Two definitions with one key are refused.</summary>
    public sealed class ChannelRegistry : IChannelRegistry
    {
        private readonly Dictionary<string, ChannelDefinition> _channels;

        public ChannelRegistry(IEnumerable<ChannelDefinition> definitions)
        {
            _channels = new Dictionary<string, ChannelDefinition>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                if (!_channels.TryAdd(definition.Key, definition))
                {
                    throw new InvalidOperationException($"The channel '{definition.Key}' is registered twice.");
                }
            }
        }

        public ChannelDefinition? Find(string key) => _channels.GetValueOrDefault(key);

        public IReadOnlyCollection<ChannelDefinition> All => _channels.Values;
    }

    /// <summary>The built-in definitions phase 1 registers.</summary>
    public static class BuiltInChannels
    {
        public static readonly ChannelDefinition Webhook = new(Channels.Webhook, AddressKind.None, ChannelAudience.SharedTargets, Passthrough: true);
        public static readonly ChannelDefinition Email = new(Channels.Email, AddressKind.Email, ChannelAudience.People | ChannelAudience.SharedTargets);
    }
}
