using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Notifications
{
    public static class ChannelRegistration
    {
        /// <summary>
        /// The phase 1 channels (webhook, email) and the registry over every registered <see cref="ChannelDefinition"/>. Called by the host and by
        /// each stage that reads channel definitions; calling it again changes nothing. The built-ins are the registry's fallback
        /// (<see cref="BuiltInChannelFallback"/>), not registered definitions, so a provider package's own definition with their key replaces them
        /// whenever and however it is registered. A registry already registered is kept.
        /// </summary>
        public static IServiceCollection AddBuiltInChannels(this IServiceCollection services)
        {
            services.TryAddSingleton(BuiltInChannelFallback.Instance);
            services.TryAddSingleton<IChannelRegistry, ChannelRegistry>();
            return services;
        }
    }
}
