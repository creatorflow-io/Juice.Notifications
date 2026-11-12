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
        public const string TenantInUse = "tenant_in_use";
        /// <summary>Another request changed the same thing at the same time; asking again gives the current answer.</summary>
        public const string ConcurrentChange = "concurrent_change";

        // Producers (phase-1-plan M2)

        /// <summary>The token's client is not a registered application.</summary>
        public const string NotAProducer = "not_a_producer";
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
        /// <summary>The request's data doesn't match its type version's schema; <c>errors</c> has a JSON pointer for each problem.</summary>
        public const string InvalidData = "invalid_data";
        /// <summary>The type doesn't allow external recipients, or the recipient's kind isn't available yet.</summary>
        public const string RecipientNotAllowed = "recipient_not_allowed";

        // Tenant configuration (phase-1-plan M4)

        /// <summary>An integration, target, route or allowlist entry is malformed; <c>errors</c> lists each field.</summary>
        public const string InvalidConfiguration = "invalid_configuration";
        /// <summary>The row comes from the deployment's configuration (ManagedBy = config); change it there.</summary>
        public const string ManagedByConfiguration = "managed_by_configuration";
        /// <summary>Deliveries went through the integration; disable it instead of deleting it.</summary>
        public const string IntegrationInUse = "integration_in_use";
        /// <summary>The target key is taken by another integration of the tenant.</summary>
        public const string TargetExists = "target_exists";
        /// <summary>Routes name the target (or one of the integration's targets); change or delete those routes first.</summary>
        public const string TargetInUse = "target_in_use";
        /// <summary>The caller doesn't administer the tenant in the path (or it doesn't exist: the answer is the same).</summary>
        public const string NotTenantAdmin = "not_tenant_admin";
    }
}
