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
            Assert.Single(new ChannelRegistry([new ChannelDefinition("hook", AddressKind.None, ChannelAudience.SharedTargets, ContentShape.Body, Passthrough: true)]).All);
        }

        [Fact]
        public void A_key_is_registered_once()
            => Assert.Throws<InvalidOperationException>(() => new ChannelRegistry([BuiltInChannels.Email, BuiltInChannels.Email with { Audience = ChannelAudience.People }]));

        [Fact]
        public void The_built_ins_are_there_once()
        {
            using var provider = new ServiceCollection().AddBuiltInChannels().AddBuiltInChannels().BuildServiceProvider();
            var channels = provider.GetRequiredService<IChannelRegistry>();
            Assert.Equal(2, channels.All.Count);
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
            Assert.Equal(2, channels.All.Count);
            Assert.Same(own, channels.Find(Channels.Webhook));
            Assert.Same(BuiltInChannels.Email, channels.Find(Channels.Email));
        }
    }
}
