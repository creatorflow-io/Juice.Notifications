namespace Juice.Notifications.Data
{
    /// <summary>
    /// The database's side of the inbox (phase-2-plan I24): <c>notifications.inbox_group(tenant, type_key)</c>, the group a type's items go to in a
    /// tenant: the tenant's choice for the type, else the group the type's latest version declares when the tenant has it, else <c>general</c>.
    /// Writing an item, restoring one and re-homing a group's items all ask it, so the rule is written once.
    /// </summary>
    public static class InboxSql
    {
        public const string GroupFunction = """
            CREATE OR REPLACE FUNCTION notifications.inbox_group(p_tenant uuid, p_type text) RETURNS text
            LANGUAGE sql STABLE AS $$
                SELECT COALESCE(
                    (SELECT t.group_key FROM notifications.type_display_groups t WHERE t.tenant_id = p_tenant AND t.type_key = p_type),
                    (SELECT g.key FROM notifications.display_groups g WHERE g.tenant_id = p_tenant AND g.key = (
                        SELECT n.display_group FROM notifications.notification_types n WHERE n.key = p_type ORDER BY n.version DESC LIMIT 1)),
                    'general')
            $$;
            """;
    }
}
