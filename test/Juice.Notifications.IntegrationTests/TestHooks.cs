using System.Runtime.CompilerServices;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The signing secret every test webhook integration names: webhooks are always signed (deferred #13), so a test integration without a
    /// secret is refused. Set in this process's environment before any test, where the service resolves it at send time.
    /// </summary>
    internal static class TestHooks
    {
        public const string SecretVariable = "NOTIFICATIONS_SECRET_TEST_HOOK";
        public const string Secret = "test-signing-secret";
        public const string SecretRef = "env:" + SecretVariable;

        [ModuleInitializer]
        internal static void SetSecret() => Environment.SetEnvironmentVariable(SecretVariable, Secret);
    }
}
