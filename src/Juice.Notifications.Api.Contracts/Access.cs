namespace Juice.Notifications.Api.Contracts
{
    /// <summary>
    /// What the signed-in caller may administer (<c>GET /api/v1/access</c>, phase-4c-plan I50): the Console builds its tenant switcher and menu
    /// from it. <see cref="AllTenants"/>: every tenant (<see cref="Tenants"/> then lists the ones the service knows, to pick from, not a
    /// limit); otherwise exactly <see cref="Tenants"/>. Both come from <c>ITenantAccess</c>. <see cref="PlatformAdmin"/>: the platform admin
    /// role, which the applications API requires (applications and their tenant links).
    /// <para>
    /// <see cref="Mode"/> (phase 5, M44): <c>service</c> or <c>embedded</c> (<see cref="AccessModes"/>). <see cref="Pages"/>: the admin page ids
    /// this deployment has, in menu order (<see cref="AdminPages"/>); a page outside it isn't mapped. A client reading an answer without them
    /// (an older service) takes <c>service</c> and every page.
    /// </para>
    /// </summary>
    public sealed record AccessResponse(bool PlatformAdmin, bool AllTenants, IReadOnlyList<AccessTenantResponse> Tenants, string Mode,
        IReadOnlyList<string> Pages);

    /// <summary>
    /// A tenant the caller administers; its name when the deployment knows it, null otherwise; and what the caller may do in it
    /// (<see cref="AccessRights"/>: <c>read</c>, <c>operate</c>, <c>configure</c>).
    /// </summary>
    public sealed record AccessTenantResponse(Guid Id, string? Name, IReadOnlyList<string> Rights);

    /// <summary>The <see cref="AccessResponse.Mode"/> values.</summary>
    public static class AccessModes
    {
        public const string Service = "service";
        public const string Embedded = "embedded";
    }

    /// <summary>
    /// The <see cref="AccessTenantResponse.Rights"/> values (phase-5-plan p5-d6). Read: pages and history. Operate: retry and replay deliveries.
    /// Configure: every change (integrations, targets, routes, mutes, policies, allowlist, templates, settings, correlation actions).
    /// </summary>
    public static class AccessRights
    {
        public const string Read = "read";
        public const string Operate = "operate";
        public const string Configure = "configure";

        public static readonly IReadOnlyList<string> All = [Read, Operate, Configure];
    }

    /// <summary>
    /// The admin page ids (<see cref="AccessResponse.Pages"/>), in menu order: every one the service has, and those an embedded host has (no
    /// people, display groups or applications). A detail page belongs to its list page's id.
    /// </summary>
    public static class AdminPages
    {
        public const string Overview = "overview";
        public const string Deliveries = "deliveries";
        public const string Notifications = "notifications";
        public const string Correlations = "correlations";
        public const string Storms = "storms";
        public const string Integrations = "integrations";
        public const string Routes = "routes";
        public const string Mutes = "mutes";
        public const string EscalationPolicies = "escalation-policies";
        public const string Allowlist = "allowlist";
        public const string Types = "types";
        public const string Settings = "settings";
        public const string DisplayGroups = "display-groups";
        public const string Subscribers = "subscribers";
        public const string Groups = "groups";
        public const string Topics = "topics";
        public const string Resolvers = "resolvers";
        public const string Applications = "applications";

        /// <summary>Every page, in menu order: the service has them all (the applications page still requires the platform admin role).</summary>
        public static readonly IReadOnlyList<string> Service =
            [Overview, Deliveries, Notifications, Correlations, Storms, Integrations, Routes, Mutes, EscalationPolicies, Allowlist, Types, DisplayGroups,
                Subscribers, Groups, Topics, Resolvers, Settings, Applications];

        /// <summary>The pages an embedded host has, in menu order: the service's less people, display groups and applications.</summary>
        public static readonly IReadOnlyList<string> Embedded =
            [Overview, Deliveries, Notifications, Correlations, Storms, Integrations, Routes, Mutes, EscalationPolicies, Allowlist, Types, Settings];
    }
}
