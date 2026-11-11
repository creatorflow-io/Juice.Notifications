using Juice.Notifications.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Notifications.Stages
{
    public static class StageRegistration
    {
        /// <summary>
        /// A stage and its runner; with <paramref name="runWorkers"/>, its background workers too. An embedding host or a test can register the
        /// stage without workers and drive <see cref="StageRunner{T, TStage}"/> itself.
        /// </summary>
        public static IServiceCollection AddStage<T, TStage>(this IServiceCollection services, StageOptions options, bool runWorkers = true)
            where T : Claimable, ITenantOwned
            where TStage : class, IStage<T>
        {
            services.TryAddSingleton<StageNudges>();
            services.TryAddScoped<TStage>();
            services.TryAddSingleton<StageRunner<T, TStage>>();
            if (runWorkers)
            {
                services.AddHostedService(sp => ActivatorUtilities.CreateInstance<StageWorker<T, TStage>>(sp, options));
            }
            return services;
        }
    }
}
