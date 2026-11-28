using System.Security.Claims;
using Juice.Notifications.Embedded;
using Juice.Notifications.Inbox;
using Juice.Notifications.People;
using Juice.Notifications.Planning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Juice.Notifications.Tests
{
    /// <summary>M54: what an embedded host may ask of its inbox, and what its sign-in keeps of a token.</summary>
    public sealed class EmbeddedInboxOptionsTests
    {
        private static NotificationsEmbeddedOptions Options(Action<EmbeddedInboxOptions>? inbox, bool set = true)
        {
            var o = new NotificationsEmbeddedOptions
            {
                ConnectionString = "Host=localhost;Database=none", TenantId = Guid.Parse("0193f0aa-0000-7000-8000-0000000b5401"), ApplicationKey = "host",
                ApplicationName = "The host",
            };
            if (set)
            {
                o.Inbox = new EmbeddedInboxOptions();
                inbox?.Invoke(o.Inbox);
            }
            return o;
        }

        [Fact]
        public void Embedded_InboxOff_IsTheHostAsItWas()
        {
            Assert.Null(new NotificationsEmbeddedOptions().Inbox);
            Assert.Null(NotificationsEmbeddedRegistration.Problem(Options(null, set: false)));
        }

        [Fact]
        public void Embedded_InboxOptions_Problem_EmptyRoles_BadRole_Duplicate_TtlBounds()
        {
            Assert.Null(NotificationsEmbeddedRegistration.Problem(Options(i => i.Roles.AddRange(["viewer", "operator", "a", "x-1", new string('a', 63)]))));

            // Empty.
            Assert.Contains("Roles", NotificationsEmbeddedRegistration.Problem(Options(_ => { })));
            // Bad: upper case, a digit or hyphen first, a space, a trailing newline (not a role), empty, too long, a colon.
            foreach (var bad in new[] { "Operator", "1op", "-op", "op er", "operator\n", "", new string('a', 64), "role:op", "op_er", "ö" })
            {
                var problem = NotificationsEmbeddedRegistration.Problem(Options(i => i.Roles.Add(bad)));
                Assert.NotNull(problem);
                // The message names the key, never the value.
                if (bad.Length > 0)
                {
                    Assert.DoesNotContain(bad, problem);
                }
            }
            // Duplicate (ordinal: a different case is a bad role, not a duplicate).
            Assert.Contains("twice", NotificationsEmbeddedRegistration.Problem(Options(i => i.Roles.AddRange(["viewer", "viewer"]))));
            // The TTL: more than zero, at most 365 days.
            Assert.Null(NotificationsEmbeddedRegistration.Problem(Options(i => { i.Roles.Add("viewer"); i.RoleTtl = TimeSpan.FromTicks(1); })));
            Assert.Null(NotificationsEmbeddedRegistration.Problem(Options(i => { i.Roles.Add("viewer"); i.RoleTtl = TimeSpan.FromDays(365); })));
            foreach (var ttl in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1), TimeSpan.FromDays(365) + TimeSpan.FromTicks(1) })
            {
                Assert.Contains("RoleTtl", NotificationsEmbeddedRegistration.Problem(Options(i => { i.Roles.Add("viewer"); i.RoleTtl = ttl; })));
            }
            Assert.Equal(TimeSpan.FromDays(30), new EmbeddedInboxOptions().RoleTtl);
        }

        [Fact]
        public void The_registration_refuses_bad_inbox_options_at_start()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

            var refused = Assert.Throws<InvalidOperationException>(() => NotificationsEmbeddedRegistration.AddJuiceNotificationsEmbedded(services,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), o =>
                {
                    o.ConnectionString = "Host=localhost;Database=none";
                    o.TenantId = Guid.NewGuid();
                    o.ApplicationKey = "host";
                    o.ApplicationName = "The host";
                    // Turned on with no role to record.
                    o.Inbox = new EmbeddedInboxOptions();
                }));

            Assert.Contains("Inbox", refused.Message);
        }

        [Fact]
        public void A_token_keeps_only_allowlisted_roles_as_written_each_once_in_byte_order()
        {
            var recorder = new SubscriberRoleRecorder(null!, new SignedInOptions(["viewer", "operator", "admin"], TimeSpan.FromDays(30)), TimeProvider.System,
                NullLogger<SubscriberRoleRecorder>.Instance);

            Assert.Equal(["admin", "operator"], recorder.Allowed(["offline_access", "operator", "Operator", "ADMIN", "admin", "operator", "uma_authorization"]));
            Assert.Empty(recorder.Allowed([]));
            Assert.Empty(recorder.Allowed(["Viewer"]));
        }

        [Fact]
        public async Task A_host_access_for_one_tenant_refuses_any_other_before_its_own_judgement()
        {
            var asked = 0;
            var access = new CountingAccess(Guid.Parse("0193f0aa-0000-7000-8000-0000000b5401"), () => asked++);

            var ct = TestContext.Current.CancellationToken;
            var other = await access.PersonAsync(new ClaimsPrincipal(), Guid.NewGuid(), ct);
            Assert.Equal((InboxAccessResult.NotAMember, 0), (other, asked));

            var own = await access.PersonAsync(new ClaimsPrincipal(), Guid.Parse("0193f0aa-0000-7000-8000-0000000b5401"), ct);
            Assert.Equal(("ann", 1), (own.Person?.Subject, asked));
        }

        private sealed class CountingAccess(Guid tenant, Action asked) : SingleTenantInboxAccess(tenant)
        {
            protected override ValueTask<InboxAccessResult> PersonAsync(ClaimsPrincipal user, CancellationToken ct)
            {
                asked();
                return ValueTask.FromResult(InboxAccessResult.Of(new InboxPerson("ann", null, [])));
            }
        }
    }
}
