using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.Tests
{
    /// <summary>The channel registry and the built-in channels' registration.</summary>
    public sealed class ChannelRegistryTests
    {
        [Fact]
        public void A_passthrough_channel_sends_a_body()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new ChannelRegistry(
                [new ChannelDefinition("chat", AddressKind.None, ChannelAudience.SharedTargets, ContentShape.Message, Passthrough: true)]));
            Assert.Contains("chat", ex.Message);
            Assert.Equal(["hook", Channels.Inbox], new ChannelRegistry([new ChannelDefinition("hook", AddressKind.None, ChannelAudience.SharedTargets, ContentShape.Body, Passthrough: true)]).All.Select(c => c.Key).Order(StringComparer.Ordinal));
        }

        [Fact]
        public void A_key_is_registered_once()
            => Assert.Throws<InvalidOperationException>(() => new ChannelRegistry([BuiltInChannels.Email, BuiltInChannels.Email with { Audience = ChannelAudience.People }]));

        [Fact]
        public void The_built_ins_are_there_once()
        {
            using var provider = new ServiceCollection().AddBuiltInChannels().AddBuiltInChannels().BuildServiceProvider();
            var channels = provider.GetRequiredService<IChannelRegistry>();
            Assert.Equal(3, channels.All.Count); // webhook, email and the inbox
            Assert.Same(BuiltInChannels.Webhook, channels.Find(Channels.Webhook));
            Assert.Same(BuiltInChannels.Email, channels.Find(Channels.Email));
        }

        /// <summary>A provider package's own definition replaces the built-in, registered after it and through a factory included.</summary>
        [Fact]
        public void A_definition_with_a_built_ins_key_replaces_it_however_it_is_registered()
        {
            var own = BuiltInChannels.Webhook with { RepeatsSafely = false };
            var services = new ServiceCollection().AddBuiltInChannels().AddSingleton(_ => own).AddBuiltInChannels();
            using var provider = services.BuildServiceProvider();
            var channels = provider.GetRequiredService<IChannelRegistry>();
            Assert.Equal(3, channels.All.Count); // webhook, email and the inbox
            Assert.Same(own, channels.Find(Channels.Webhook));
            Assert.Same(BuiltInChannels.Email, channels.Find(Channels.Email));
        }

        /// <summary>The inbox is the core's own (phase-2-plan I17): always there, even with no built-ins, and no package defines it.</summary>
        [Fact]
        public void The_inbox_is_always_there_and_no_package_defines_it()
        {
            Assert.Same(BuiltInChannels.Inbox, new ChannelRegistry([]).Find(Channels.Inbox));
            var ex = Assert.Throws<InvalidOperationException>(() => new ChannelRegistry([BuiltInChannels.Inbox with { RepeatsSafely = false }]));
            Assert.Contains("inbox", ex.Message);
        }
    }
}
