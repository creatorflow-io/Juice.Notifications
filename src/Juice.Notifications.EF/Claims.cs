namespace Juice.Notifications.Data
{
    /// <summary>A row a worker claimed: its key, and the version the claim gave it, which every write of the result must still find (design D11).</summary>
    public sealed record Claim(Guid TenantId, Guid Id, int Version);

    /// <summary>
    /// Claims due rows for a worker (design §11). One implementation per database, each with the concurrency suite. A claim is its own short
    /// transaction: due rows (<c>NextAt &lt;= now</c>, no live lease) get the worker's name, a lease and <c>Version + 1</c>, and two workers never get
    /// the same row. Writing the result is a normal EF save: <see cref="Claimable.Version"/> is a concurrency token, so a save by a worker whose
    /// lease ran out (someone claimed the row since) affects nothing and throws.
    /// </summary>
    public interface IClaimStore
    {
        /// <summary>Up to <paramref name="batch"/> due rows of <typeparamref name="T"/>, oldest due first.</summary>
        Task<IReadOnlyList<Claim>> ClaimAsync<T>(string worker, int batch, TimeSpan lease, DateTimeOffset now, CancellationToken ct)
            where T : Claimable, ITenantOwned;
    }
}
