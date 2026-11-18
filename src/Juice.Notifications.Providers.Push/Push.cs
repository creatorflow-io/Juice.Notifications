using System.Text;
using System.Text.Json;
using Juice.Notifications.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Push
{
    /// <summary>
    /// The <c>push</c> channel (design §9.2, phase-4-plan I42): people's devices, one delivery per device, through the integration whose kind is
    /// the device's platform. Its content is a chat message (title, text, link): what a notification on a phone or in a browser shows.
    /// </summary>
    public static class PushChannel
    {
        public const string Key = "push";

        public static readonly ChannelDefinition Definition = new(Key, AddressKind.Device, ChannelAudience.People, ContentShape.Chat);
    }

    /// <summary>The client the push providers share (as <see cref="ProviderHttp.Handler"/>, HTTP/2 for APNs); tests replace it.</summary>
    public sealed class PushHttp(HttpMessageHandler handler) : IDisposable
    {
        public HttpClient Client { get; } = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        public static PushHttp Create(DeliveryOptions options) => new(ProviderHttp.Handler(options));

        public void Dispose() => Client.Dispose();
    }

    internal static class PushContent
    {
        /// <summary>What every platform shows: the title, the text (cut to fit a push payload), the link.</summary>
        public static ChatContent? Read(string content)
        {
            try
            {
                return JsonSerializer.Deserialize<ChatContent>(content);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static string Body(ChatContent content, int max = 1000) => ChannelContent.Cut(content.Text, max)!;

        public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromBase64Url(string text)
        {
            var s = text.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }

        /// <summary>A compact JWS: <c>header.claims.signature</c>, the signature from <paramref name="sign"/> over the first two parts.</summary>
        public static string Jwt(object header, object claims, Func<byte[], byte[]> sign)
        {
            var signing = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
            return signing + "." + Base64Url(sign(Encoding.ASCII.GetBytes(signing)));
        }
    }

    public static class PushRegistration
    {
        /// <summary>The <c>push</c> channel and its providers: <c>fcm</c>, <c>apns</c> and <c>webpush</c>, each the platform of the devices it reaches.</summary>
        public static IServiceCollection AddPush(this IServiceCollection services)
        {
            services.AddSingleton(PushChannel.Definition);
            services.TryAddSingleton(sp => PushHttp.Create(sp.GetRequiredService<IOptions<DeliveryOptions>>().Value));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, FcmProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, ApnsProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, WebPushProvider>());
            return services;
        }
    }
}
