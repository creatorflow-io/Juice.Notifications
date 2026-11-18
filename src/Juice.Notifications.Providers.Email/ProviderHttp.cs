using System.Net.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Email
{
    /// <summary>
    /// The HTTP client the email providers and their callback handlers share: no redirects, the deployment's connect timeout, pooled connections
    /// renewed every few minutes (DNS changes reach it). Replaced in tests by one over a fake of the provider's API.
    /// </summary>
    public sealed class EmailHttp(HttpMessageHandler handler) : IDisposable
    {
        public HttpClient Client { get; } = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        public static EmailHttp Create(DeliveryOptions options) => new(ProviderHttp.Handler(options));

        public void Dispose() => Client.Dispose();
    }

    /// <summary>Email addresses as SES and SendGrid take them.</summary>
    internal static class EmailAddresses
    {
        /// <summary>The bare sender address, checked when an integration is saved.</summary>
        public static bool IsAddress(string? value)
            => value is { Length: > 0 and <= 254 } && value == value.Trim() && !value.Contains('<') && !value.Contains('>')
            && MailAddress.TryCreate(value, out var parsed) && parsed.Address == value && parsed.Host.Length > 0;

        public static bool IsRecipient(string value) => MailAddress.TryCreate(value, out var parsed) && parsed.Address == value;
    }

    public static class EmailProviderRegistration
    {
        /// <summary>
        /// The SES and SendGrid providers (integration kinds <c>ses</c> and <c>sendgrid</c>, channel <c>email</c>) and their callback handlers
        /// (<c>/api/v1/callbacks/ses/…</c>, <c>/api/v1/callbacks/sendgrid/…</c>).
        /// </summary>
        public static IServiceCollection AddEmailProviders(this IServiceCollection services)
        {
            services.TryAddSingleton(sp => EmailHttp.Create(sp.GetRequiredService<IOptions<DeliveryOptions>>().Value));
            services.TryAddSingleton<SnsCertificates>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, SesProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, SendGridProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallbackHandler, SesCallbackHandler>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallbackHandler, SendGridCallbackHandler>());
            return services;
        }
    }
}
