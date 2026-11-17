using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Juice.Notifications.Tests.ProviderContracts
{
    /// <summary>
    /// The shape every HTTP provider in phase 4 follows, kept small: a JSON email API that takes <c>POST /send</c> with an <c>Idempotency-Key</c>
    /// header and answers <c>202 {"id"}</c>, or an error <c>{"code"}</c>. It shows the contract kit at work and is the template the real
    /// providers start from (how a status and an error code become an outcome, and where the timeout lies).
    /// </summary>
    internal sealed class ReferenceHttpProvider(HttpClient http, TimeSpan timeout) : INotificationProvider
    {
        public string Channel => Channels.Email;
        public string Kind => "reference";
        public bool Idempotent => true;
        public bool RequiresSecret => true;

        public string? CheckSettings(string settings) => null;

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.test/send")
            {
                Content = JsonContent.Create(new { to = delivery.Destination, content = JsonDocument.Parse(delivery.Content).RootElement }),
            };
            request.Headers.Authorization = new("Bearer", integration.Secret);
            request.Headers.Add("Idempotency-Key", delivery.DeliveryId.ToString());
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, deadline.Token);
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
            {
                return SendResult.Retry(FailureCategory.Provider, "Couldn't connect: " + ex.Message);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException && !ct.IsCancellationRequested)
            {
                // The request may have reached the provider: an unknown outcome, safe to repeat since the key is the delivery id.
                return SendResult.Unknown("No answer: " + ex.Message);
            }
            using (response)
            {
                var status = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(ct);
                string? Field(string name)
                {
                    try
                    {
                        return JsonDocument.Parse(body).RootElement.TryGetProperty(name, out var v) ? v.GetString() : null;
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                }
                return status switch
                {
                    >= 200 and < 300 => SendResult.Accepted(Field("id")),
                    401 or 403 => SendResult.Permanent(FailureCategory.Provider, $"{status}: credentials refused", status),
                    429 => SendResult.Retry(FailureCategory.RateLimited, "429: throttled", status),
                    400 or 422 when Field("code") is "invalid_recipient" => SendResult.Gone($"{status}: the address can't receive", status),
                    400 or 413 or 422 => SendResult.Permanent(FailureCategory.Content, $"{status}: {Field("code") ?? "refused"}", status),
                    >= 500 => SendResult.Retry(FailureCategory.Provider, $"{status}: the provider is unavailable", status),
                    _ => SendResult.Permanent(FailureCategory.Provider, $"{status}: unexpected", status),
                };
            }
        }
    }

    /// <summary>The contract kit (phase-4-plan p4-d1) over <see cref="ReferenceHttpProvider"/>.</summary>
    public sealed class ReferenceProviderTests : ProviderContract
    {
        private readonly ReferenceHttpProvider _provider;

        public ReferenceProviderTests() => _provider = new ReferenceHttpProvider(Api.Client(), TimeSpan.FromMilliseconds(200));

        protected override INotificationProvider Provider => _provider;

        protected override IntegrationContext Integration => new(Guid.CreateVersion7(), "ref", "reference", "{}", "api-key");

        protected override RenderedDelivery Delivery(Guid id, int attempt)
            => new(id, Guid.CreateVersion7(), Channels.Email, """{"subject":"s","text":"t"}""", "pat@example.com", attempt, NotificationPriority.Normal, false);

        protected override bool Arrange(ContractCase @case)
        {
            switch (@case)
            {
                case ContractCase.Accepted: Api.Then(HttpStatusCode.Accepted, """{"id":"m-1"}"""); break;
                case ContractCase.ServerError: Api.Then(HttpStatusCode.ServiceUnavailable); break;
                case ContractCase.RateLimited: Api.Then(HttpStatusCode.TooManyRequests, "", "application/json", ("Retry-After", "1")); break;
                case ContractCase.CredentialsRefused: Api.Then(HttpStatusCode.Unauthorized, """{"code":"bad_key"}"""); break;
                case ContractCase.AddressRefused: Api.Then(HttpStatusCode.UnprocessableEntity, """{"code":"invalid_recipient"}"""); break;
                case ContractCase.ContentRefused: Api.Then(HttpStatusCode.RequestEntityTooLarge, """{"code":"too_large"}"""); break;
                case ContractCase.ConnectionRefused: Api.ThenRefused(); break;
                case ContractCase.NoAnswer: Api.ThenSilent(); break;
            }
            return true;
        }

        protected override string? IdempotencyKey(FakeHttpApi.Seen request) => request.Headers.TryGetValue("Idempotency-Key", out var key) ? key : null;

        /// <summary>An address the API says can't receive is gone, so a person's contact point fails (phase-2-plan M13).</summary>
        [Fact]
        public async Task A_refused_address_marks_the_address_gone()
        {
            Arrange(ContractCase.AddressRefused);
            Assert.True((await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct)).AddressGone);
        }
    }
}
