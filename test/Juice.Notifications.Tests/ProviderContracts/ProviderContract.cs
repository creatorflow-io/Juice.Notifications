using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Juice.Notifications.Tests.ProviderContracts
{
    /// <summary>
    /// A provider's HTTP API, in process (phase-4-plan p4-d1): the provider's <see cref="HttpClient"/> sends through it, and each request is
    /// answered by the next scripted answer (then <see cref="Default"/>). It keeps every request with its body, so a suite can check what the
    /// provider sent: the idempotency key, the credentials, the payload.
    /// </summary>
    public sealed class FakeHttpApi : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();

        /// <summary>One request as it arrived: the message (its content read out into <see cref="Body"/>).</summary>
        public sealed record Seen(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body);

        public ConcurrentQueue<Seen> Requests { get; } = new();

        /// <summary>The answer once the script is used up.</summary>
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Default { get; set; }
            = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        public HttpClient Client() => new(this, disposeHandler: false);

        public FakeHttpApi Then(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        {
            _script.Enqueue(answer);
            return this;
        }

        /// <summary>Answers with a status and a body (JSON unless another type is given).</summary>
        public FakeHttpApi Then(HttpStatusCode status, string body = "", string contentType = "application/json", params (string Name, string Value)[] headers)
            => Then((_, _) =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
                foreach (var (name, value) in headers)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }
                return Task.FromResult(response);
            });

        /// <summary>The connection is refused before anything was sent.</summary>
        public FakeHttpApi ThenRefused()
            => Then((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused", new System.Net.Sockets.SocketException(10061)));

        /// <summary>The request was read, and no answer comes: the provider's own timeout ends it (an unknown outcome).</summary>
        public FakeHttpApi ThenSilent() => Then(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Enqueue(new Seen(request.Method, request.RequestUri!, headers, body));
            var answer = _script.TryDequeue(out var next) ? next : Default;
            var response = await answer(request, ct);
            response.RequestMessage = request;
            return response;
        }
    }

    /// <summary>What a provider is asked to cope with: each a situation every provider meets, in its own API's words.</summary>
    public enum ContractCase
    {
        /// <summary>The provider took the message (and gave its id).</summary>
        Accepted,
        /// <summary>A 5xx, or the provider saying it is unavailable: retry, and fail over once the attempts run out.</summary>
        ServerError,
        /// <summary>Throttled: wait and retry on the same integration, never fail over.</summary>
        RateLimited,
        /// <summary>Our credentials or account are refused: a provider failure no retry fixes, so fail over.</summary>
        CredentialsRefused,
        /// <summary>The address doesn't exist or can't receive: no integration helps.</summary>
        AddressRefused,
        /// <summary>The message itself is refused (too large, malformed): no integration helps.</summary>
        ContentRefused,
        /// <summary>No connection: nothing went out, retry.</summary>
        ConnectionRefused,
        /// <summary>The request went out and no answer came: unknown.</summary>
        NoAnswer,
    }

    /// <summary>
    /// The contract every provider passes against its fake (phase-4-plan p4-d1, I39; design §9.3, §10.2, §10.3): how its API's answers become
    /// outcomes and categories, which decide retries and failover; that it never throws for a failed send; and that an idempotent provider
    /// sends the delivery id as its key on every attempt. A provider's suite derives from this, scripts its fake for each case in its API's own
    /// words (<see cref="Arrange"/>), and adds tests of its own for the rest.
    /// </summary>
    public abstract class ProviderContract
    {
        protected static CancellationToken Ct => TestContext.Current.CancellationToken;

        protected FakeHttpApi Api { get; } = new();

        /// <summary>The provider under test, sending through <see cref="Api"/>, with a short timeout so <see cref="ContractCase.NoAnswer"/> ends fast.</summary>
        protected abstract INotificationProvider Provider { get; }

        protected abstract IntegrationContext Integration { get; }

        /// <summary>A delivery the provider can send: its channel's content and an address.</summary>
        protected abstract RenderedDelivery Delivery(Guid id, int attempt);

        /// <summary>Scripts <see cref="Api"/> to answer as the provider's API does in this case; false when the API has no such answer.</summary>
        protected abstract bool Arrange(ContractCase @case);

        /// <summary>The idempotency key the provider sent in a request (a header or a body field), for idempotent providers.</summary>
        protected virtual string? IdempotencyKey(FakeHttpApi.Seen request) => null;

        private async Task<SendResult> SendAsync(ContractCase @case)
        {
            Assert.SkipUnless(Arrange(@case), $"{Provider.Kind}'s API has no answer for {@case}");
            return await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);
        }

        [Fact]
        public async Task Accepted_is_accepted_or_delivered()
        {
            var result = await SendAsync(ContractCase.Accepted);
            Assert.Contains(result.Outcome, new[] { AttemptOutcome.Accepted, AttemptOutcome.Delivered });
            Assert.Null(result.Category);
            Assert.Single(Api.Requests);
        }

        [Fact]
        public async Task A_server_error_is_a_retryable_provider_failure()
            => Assert.Equal((AttemptOutcome.RetryableFailure, FailureCategory.Provider), Outcome(await SendAsync(ContractCase.ServerError)));

        [Fact]
        public async Task Throttling_is_rate_limited_so_it_waits_and_never_fails_over()
            => Assert.Equal((AttemptOutcome.RetryableFailure, FailureCategory.RateLimited), Outcome(await SendAsync(ContractCase.RateLimited)));

        [Fact]
        public async Task Refused_credentials_are_a_permanent_provider_failure_which_fails_over()
            => Assert.Equal((AttemptOutcome.PermanentFailure, FailureCategory.Provider), Outcome(await SendAsync(ContractCase.CredentialsRefused)));

        [Fact]
        public async Task A_refused_address_is_a_destination_failure_which_never_fails_over()
            => Assert.Equal((AttemptOutcome.PermanentFailure, FailureCategory.Destination), Outcome(await SendAsync(ContractCase.AddressRefused)));

        [Fact]
        public async Task Refused_content_is_a_content_failure_which_never_fails_over()
            => Assert.Equal((AttemptOutcome.PermanentFailure, FailureCategory.Content), Outcome(await SendAsync(ContractCase.ContentRefused)));

        [Fact]
        public async Task No_connection_is_retryable_since_nothing_went_out()
            => Assert.Equal(AttemptOutcome.RetryableFailure, (await SendAsync(ContractCase.ConnectionRefused)).Outcome);

        [Fact]
        public async Task No_answer_after_the_request_is_an_unknown_outcome()
            => Assert.Equal(AttemptOutcome.Unknown, (await SendAsync(ContractCase.NoAnswer)).Outcome);

        [Fact]
        public async Task An_idempotent_provider_sends_the_delivery_id_as_its_key_on_every_attempt()
        {
            Assert.SkipUnless(Provider.Idempotent, $"{Provider.Kind} isn't idempotent");
            var id = Guid.CreateVersion7();
            Arrange(ContractCase.ServerError);
            Arrange(ContractCase.Accepted);
            await Provider.SendAsync(Delivery(id, 1), Integration, Ct);
            await Provider.SendAsync(Delivery(id, 2), Integration, Ct);
            var keys = Api.Requests.Select(IdempotencyKey).ToList();
            Assert.Equal(2, keys.Count);
            Assert.All(keys, k => Assert.Equal(id.ToString(), k));
        }

        private static (AttemptOutcome, FailureCategory?) Outcome(SendResult result) => (result.Outcome, result.Category);
    }
}
