namespace Juice.Notifications
{
    /// <summary>
    /// Error codes: the <c>code</c> in the API's problem details, and what commands return. Stable, so clients can branch on them. The handlers and
    /// the API both use these constants, so a code can't drift between them.
    /// </summary>
    public static class ErrorCodes
    {
        public const string NotFound = "not_found";
        public const string InvalidApplication = "invalid_application";
        public const string InvalidTenant = "invalid_tenant";
        public const string ApplicationExists = "application_exists";
        public const string ClientIdInUse = "client_id_in_use";
        public const string BrokerUserInUse = "broker_user_in_use";
        public const string TenantInUse = "tenant_in_use";
        /// <summary>Another request changed the same thing at the same time; asking again gives the current answer.</summary>
        public const string ConcurrentChange = "concurrent_change";

        // Producers (phase-1-plan M2)

        /// <summary>A client's own token (client credentials) on the person's API (<c>me</c>): a service account is no person.</summary>
        public const string NotAPerson = "not_a_person";
        /// <summary>The token's client is not a registered application.</summary>
        public const string NotAProducer = "not_a_producer";
        /// <summary>A RabbitMQ message without a broker-verified <c>user_id</c>: nothing says which application sent it.</summary>
        public const string UnverifiedPublisher = "unverified_publisher";
        /// <summary>The application is disabled, so it can't declare types or send.</summary>
        public const string ApplicationDisabled = "application_disabled";
        /// <summary>The caller is a different application than the one in the path.</summary>
        public const string NotYourApplication = "not_your_application";
        /// <summary>A type declaration is malformed; <c>errors</c> lists each field.</summary>
        public const string InvalidType = "invalid_type";
        /// <summary>The application isn't linked to the tenant it sends for.</summary>
        public const string TenantNotLinked = "tenant_not_linked";
        /// <summary>The type key is outside the application's namespace (<c>{application}.…</c>).</summary>
        public const string TypeNotOwned = "type_not_owned";
        /// <summary>No such type, or no such version of it, for this application.</summary>
        public const string UnknownType = "unknown_type";
        /// <summary>A request is malformed; <c>errors</c> lists each field.</summary>
        public const string InvalidRequest = "invalid_request";
        /// <summary>
        /// Embedded (phase-5-plan I59): a client call with no transaction of the host's open. The request is written in the host's transaction,
        /// so it commits or rolls back with the host's own change; without one there is nothing to write it in.
        /// </summary>
        public const string NoTransaction = "no_transaction";
        /// <summary>The request's data doesn't match its type version's schema; <c>errors</c> has a JSON pointer for each problem.</summary>
        public const string InvalidData = "invalid_data";
        /// <summary>The type doesn't allow external recipients, or the recipient's kind isn't available yet.</summary>
        public const string RecipientNotAllowed = "recipient_not_allowed";

        // Tenant configuration (phase-1-plan M4)

        /// <summary>An integration, target, route or allowlist entry is malformed; <c>errors</c> lists each field.</summary>
        public const string InvalidConfiguration = "invalid_configuration";
        /// <summary>The row comes from the deployment's configuration (ManagedBy = config); change it there.</summary>
        public const string ManagedByConfiguration = "managed_by_configuration";
        /// <summary>
        /// The row belongs to a producer's compatibility alias (ManagedBy = webhooks-alias, phase 5 M46) until cutover; change it through the
        /// producer's own API.
        /// </summary>
        public const string ManagedByAlias = "managed_by_alias";
        /// <summary>The row was made through the API; the webhooks alias (phase 5 M46) leaves it alone.</summary>
        public const string ManagedByApi = "managed_by_api";
        /// <summary>Deliveries went through the integration; disable it instead of deleting it.</summary>
        /// <summary>The id a create named is another row's, or a row's id would change (phase 5 M46-2: ids an in-process caller keeps).</summary>
        public const string IdInUse = "id_in_use";
        public const string IntegrationInUse = "integration_in_use";
        /// <summary>The target key is taken by another integration of the tenant.</summary>
        public const string TargetExists = "target_exists";
        /// <summary>Routes name the target (or one of the integration's targets); change or delete those routes first.</summary>
        public const string TargetInUse = "target_in_use";
        /// <summary>The caller doesn't administer the tenant in the path (or it doesn't exist: the answer is the same).</summary>
        public const string NotTenantAdmin = "not_tenant_admin";
        /// <summary>The caller may read this tenant's notifications but not do this (an admin right it lacks: operate or configure).</summary>
        public const string NotAllowed = "not_allowed";
        /// <summary>A delivery that isn't failed, was never rendered, or whose request has expired can't be retried.</summary>
        public const string NotRetryable = "not_retryable";
        /// <summary>The identity is already an alias of another subscriber: unlink it there first.</summary>
        public const string AliasInUse = "alias_in_use";
        /// <summary>The subscriber is linked into another one (or others are linked into it), so it can't be linked as asked.</summary>
        public const string SubscriberMerged = "subscriber_merged";
        /// <summary>People can't subscribe themselves to this topic; an admin adds them.</summary>
        public const string TopicNotSelfService = "topic_not_self_service";
        /// <summary>The caller's token doesn't belong to the tenant in the path, or names no person (no subject).</summary>
        public const string NotTenantMember = "not_tenant_member";

        // The tenant pattern (juice-docs decision record): the request's tenant, checked before the endpoint

        /// <summary>The token names another tenant than the path (or one that isn't a tenant Id), or the tenant is unknown to a caller who may not
        /// see every tenant: the answer is the same, so it doesn't reveal which tenants exist.</summary>
        public const string TenantForbidden = "tenant_forbidden";
        /// <summary>No such tenant (to a platform admin).</summary>
        public const string TenantNotFound = "tenant_not_found";
        /// <summary>The tenant isn't active (inactive, suspended, abandoned or never approved).</summary>
        public const string TenantInactive = "tenant_inactive";
        /// <summary>The tenant is still being set up (503): try again later.</summary>
        public const string TenantStarting = "tenant_starting";
        /// <summary>The person already has this address on this channel.</summary>
        public const string ContactPointExists = "contact_point_exists";
        /// <summary>The person has as many contact points (or verification sends) as allowed; remove one, or wait.</summary>
        public const string ContactPointLimit = "contact_point_limit";
        /// <summary>A verification code is sent at most once a minute per contact point, and a few times an hour per person.</summary>
        public const string TooManyRequests = "too_many_requests";
        /// <summary>The code is wrong; the response says how many tries are left.</summary>
        public const string VerificationFailed = "verification_failed";
        /// <summary>No code is outstanding: none was sent, it expired, its tries ran out, or the address is already verified.</summary>
        public const string VerificationExpired = "verification_expired";
        /// <summary>The contact point is verified and working; there is nothing to verify.</summary>
        public const string AlreadyVerified = "already_verified";
        /// <summary>The tenant has no enabled integration for the channel, so a code can't be sent.</summary>
        public const string ChannelUnavailable = "channel_unavailable";
        /// <summary>The service's own application, types or inbox integration, which the API doesn't change.</summary>
        public const string ManagedByService = "managed_by_service";
        /// <summary>As many mutes are in force or to come as allowed (a person 100, a tenant 500); cancel one first.</summary>
        public const string MuteLimit = "mute_limit";

        // Correlations (phase-3-plan M20)
        /// <summary>The correlation is resolved or cancelled: it can't be acknowledged or acted on (a new request reopens it).</summary>
        public const string CorrelationClosed = "correlation_closed";

        // Display groups and the inbox (phase-2-plan M15)

        /// <summary>The tenant has as many display groups as allowed (50).</summary>
        public const string DisplayGroupLimit = "display_group_limit";
        /// <summary>One of the three groups every tenant has: renamed and reordered, never deleted.</summary>
        public const string DisplayGroupBuiltIn = "display_group_built_in";

        // Provider callbacks (phase-4-plan M28)

        /// <summary>The integration's provider is set up in its own console, not told where its callbacks go by the service.</summary>
        public const string CallbacksNotRegistered = "callbacks_not_registered";
        /// <summary>The deployment's <c>Callbacks:PublicUrl</c> isn't set, so there is no address to give the provider.</summary>
        public const string PublicUrlUnset = "public_url_unset";
        /// <summary>The provider refused, or couldn't be reached; the detail says what it answered.</summary>
        public const string ProviderRefused = "provider_refused";
    }
}
