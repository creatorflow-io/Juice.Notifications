using Juice.Notifications.Data;
using Juice.Notifications.Planning;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Tests
{
    /// <summary>The expansion cache for remote resolvers (phase-2-plan I23).</summary>
    public sealed class ResolverCacheTests
    {
        private sealed class Clock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now = now;

            public override DateTimeOffset GetUtcNow() => Now;
        }

        private static readonly Guid Tenant = Guid.NewGuid();
        private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
        private int _loads;

        private ResolverCache Cache(int seconds = 300) => new(Options.Create(new ResolverOptions { CacheSeconds = seconds }), _clock);

        private static RecipientResolverConfig Config(string settings = "{}") => new()
        {
            TenantId = Tenant, Key = "staff", Kind = "keycloak", Handles = ["group"], Settings = settings, DefinitionHash = "h", UpdatedAt = default,
        };

        private ValueTask<ResolveResult> Load(CancellationToken ct)
        {
            _loads++;
            return ValueTask.FromResult(new ResolveResult([]) { People = [new ResolvedPerson("oidc", "p" + _loads)] });
        }

        private static string First(ResolveResult result) => result.People[0].ExternalId;

        [Fact]
        public async Task An_expansion_is_reused_until_it_expires()
        {
            var cache = Cache();
            Assert.Equal("p1", First(await cache.GetOrLoadAsync(Config(), "group:sre", Load, TestContext.Current.CancellationToken)));
            _clock.Now = _clock.Now.AddSeconds(299);
            Assert.Equal("p1", First(await cache.GetOrLoadAsync(Config(), "group:sre", Load, TestContext.Current.CancellationToken)));
            _clock.Now = _clock.Now.AddSeconds(1);
            Assert.Equal("p2", First(await cache.GetOrLoadAsync(Config(), "group:sre", Load, TestContext.Current.CancellationToken)));
        }

        [Fact]
        public async Task Another_name_or_a_changed_configuration_loads_afresh()
        {
            var cache = Cache();
            await cache.GetOrLoadAsync(Config(), "group:sre", Load, TestContext.Current.CancellationToken);
            Assert.Equal("p2", First(await cache.GetOrLoadAsync(Config(), "group:ops", Load, TestContext.Current.CancellationToken)));
            Assert.Equal("p3", First(await cache.GetOrLoadAsync(Config("""{"realm":"other"}"""), "group:sre", Load, TestContext.Current.CancellationToken)));
        }

        /// <summary>A listing cut short (the step's deadline, the bound) isn't kept: the next plan reads it again, whole if the directory has recovered.</summary>
        [Fact]
        public async Task A_truncated_expansion_is_not_remembered()
        {
            var cache = Cache();
            ValueTask<ResolveResult> Partial(CancellationToken ct)
            {
                _loads++;
                return ValueTask.FromResult(new ResolveResult([], Truncated: true) { People = [new ResolvedPerson("oidc", "p" + _loads)], Partial = true });
            }
            Assert.Equal("p1", First(await cache.GetOrLoadAsync(Config(), "group:sre", Partial, TestContext.Current.CancellationToken)));
            Assert.Equal("p2", First(await cache.GetOrLoadAsync(Config(), "group:sre", Load, TestContext.Current.CancellationToken)));
            Assert.Equal("p2", First(await cache.GetOrLoadAsync(Config(), "group:sre", Load, TestContext.Current.CancellationToken)));
        }

        [Fact]
        public async Task A_failed_load_is_not_remembered()
        {
            var cache = Cache();
            await Assert.ThrowsAsync<ResolverUnavailableException>(async () =>
                await cache.GetOrLoadAsync(Config(), "group:sre", _ => throw new ResolverUnavailableException("down"), TestContext.Current.CancellationToken));
            Assert.Equal("p1", First(await cache.GetOrLoadAsync(Config(), "group:sre", Load, TestContext.Current.CancellationToken)));
        }

        [Fact]
        public void Code_point_order_is_byte_order_not_utf16_order()
        {
            // U+FF21 is one UTF-16 unit above the surrogates, but below any code point past U+FFFF.
            const string fullwidth = "Ａ", emoji = "\U0001F600";
            Assert.True(string.CompareOrdinal(emoji, fullwidth) < 0);
            Assert.True(CodePointOrder.Compare(fullwidth, emoji) < 0);
            Assert.True(CodePointOrder.Compare("A", "a") < 0);
            Assert.True(CodePointOrder.Compare("a", "ab") < 0);
            Assert.Equal(0, CodePointOrder.Compare("ab", "ab"));
            Assert.Equal(["oidc:x", "oidc:y", "x:a"], new[] { new ResolvedPerson("x", "a"), new ResolvedPerson("oidc", "y"), new ResolvedPerson("oidc", "x") }
                .Order(CodePointOrder.People).Select(p => p.Source + ":" + p.ExternalId));
        }

        [Fact]
        public async Task A_result_past_the_people_bound_is_not_kept()
        {
            var cache = Cache();
            var huge = new ResolveResult([]) { People = [.. Enumerable.Range(0, ResolverCache.MaxPeople + 1).Select(i => new ResolvedPerson("oidc", "p"))] };
            var loads = 0;
            ValueTask<ResolveResult> LoadHuge(CancellationToken _)
            {
                loads++;
                return ValueTask.FromResult(huge);
            }
            await cache.GetOrLoadAsync(Config(), "group:all", LoadHuge, TestContext.Current.CancellationToken);
            await cache.GetOrLoadAsync(Config(), "group:all", LoadHuge, TestContext.Current.CancellationToken);
            Assert.Equal(2, loads);
        }

        [Fact]
        public async Task Zero_seconds_turns_caching_off()
        {
            var cache = Cache(0);
            await cache.GetOrLoadAsync(Config(), "group:sre", Load, TestContext.Current.CancellationToken);
            Assert.Equal("p2", First(await cache.GetOrLoadAsync(Config(), "group:sre", Load, TestContext.Current.CancellationToken)));
        }
    }
}
