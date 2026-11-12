namespace Juice.Notifications.Data
{
    /// <summary>A row a worker claimed: its key, and the version the claim gave it, which every write of the result must still find (design D11).</summary>
    public sealed record Claim(Guid TenantId, Guid Id, int Version);

    /// <summary>A claim handed back: the row's new version, how many steps on it have now failed in a row, and when it is due again.</summary>
    public sealed record Released(int Version, int StepFailures, DateTimeOffset? NextAt);

    /// <summary>
    /// Claims due rows for a worker (design §11). One implementation per database, each with the concurrency suite. A claim is its own short
    /// transaction: due rows (<c>NextAt &lt;= now</c>, no live lease) get the worker's name, a lease and <c>Version + 1</c>, and two workers never get
    /// the same row. Writing the result is a normal EF save: <see cref="Claimable.Version"/> is a concurrency token, so a save by a worker whose
    /// lease ran out (someone claimed the row since) affects nothing and throws.
    /// <para>
    /// Leases are measured on the database's clock, the one clock every host shares: a host whose clock runs ahead can't take a row whose lease
    /// is still live. When a row is due is the application's clock, since the application writes <see cref="Claimable.NextAt"/>.
    /// </para>
    /// </summary>
    public interface IClaimStore
    {
        /// <summary>Up to <paramref name="batch"/> rows of <typeparamref name="T"/> due at <paramref name="now"/>, oldest due first.</summary>
        Task<IReadOnlyList<Claim>> ClaimAsync<T>(string worker, int batch, TimeSpan lease, DateTimeOffset now, CancellationToken ct)
            where T : Claimable, ITenantOwned;

        /// <summary>
        /// Gives the claim a fresh lease from now, before its step runs, so a step late in a batch still holds a whole lease. False when the row
        /// was claimed again since (its version moved): the step must not run.
        /// </summary>
        Task<bool> RenewAsync<T>(Claim claim, TimeSpan lease, CancellationToken ct) where T : Claimable, ITenantOwned;

        /// <summary>
        /// Hands a claim back without a step's result: the lease is cleared and the version moves on, in one statement. With
        /// <paramref name="failure"/>, one more failed step is counted and the row is due after the backoff that count earns; otherwise
        /// <paramref name="nextAt"/> replaces when it is due (null keeps it). Null when the row was claimed again since.
        /// </summary>
        Task<Released?> ReleaseAsync<T>(Claim claim, DateTimeOffset? nextAt, FailureBackoff? failure, CancellationToken ct) where T : Claimable, ITenantOwned;
    }

    /// <summary>After a failed step: due at <c>Now + Base × 2^(failures − 1)</c>, at most <c>Now + Max</c>.</summary>
    public sealed record FailureBackoff(DateTimeOffset Now, TimeSpan Base, TimeSpan Max);
}
