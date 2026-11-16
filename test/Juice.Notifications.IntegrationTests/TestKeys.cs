using System.Runtime.CompilerServices;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The contact encryption keys (Encryption:KeyRef and HashKeyRef name these variables by default), set in this process's environment before
    /// any test, unless the environment already has them.
    /// </summary>
    internal static class TestKeys
    {
        public const string KeyVariable = "NOTIFICATIONS_CONTACT_KEY";
        public const string HashKeyVariable = "NOTIFICATIONS_CONTACT_HASH_KEY";

        [ModuleInitializer]
        internal static void SetKeys()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(KeyVariable)))
            {
                Environment.SetEnvironmentVariable(KeyVariable, Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
            }
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(HashKeyVariable)))
            {
                Environment.SetEnvironmentVariable(HashKeyVariable, Convert.ToBase64String(Enumerable.Range(101, 32).Select(i => (byte)i).ToArray()));
            }
        }
    }
}
