namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Where <see cref="LocalStack"/> finds <c>deploy/local</c>: own checkout, then the environment variable, then the main worktree. Needs no stack.</summary>
    public sealed class LocalStackResolutionTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "localstack-" + Guid.NewGuid().ToString("N"));

        public void Dispose() => Directory.Delete(_root, true);

        private string Deploy(string checkout, bool withEnv = true)
        {
            var folder = Path.Combine(_root, checkout, "deploy", "local");
            Directory.CreateDirectory(folder);
            if (withEnv)
            {
                File.WriteAllText(Path.Combine(folder, ".env"), "POSTGRES_PASSWORD=x");
            }
            return folder;
        }

        private string Start(string checkout)
        {
            var dir = Path.Combine(_root, checkout, "test", "bin");
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void The_checkouts_own_folder_wins_over_the_variable_and_the_main_worktree()
        {
            var own = Deploy("wt");
            var named = Deploy("named");
            var main = Deploy("main");
            var found = LocalStack.ResolveDeployFolder(Start("wt"), _ => named, _ => Path.Combine(_root, "main", ".git"));
            Assert.Equal(own, found);
        }

        [Fact]
        public void The_variable_wins_over_the_main_worktree()
        {
            Deploy("wt", withEnv: false);
            var named = Deploy("named");
            Deploy("main");
            var found = LocalStack.ResolveDeployFolder(Start("wt"), name => name == LocalStack.DeployFolderVariable ? named : null, _ => Path.Combine(_root, "main", ".git"));
            Assert.Equal(named, found);
        }

        [Fact]
        public void A_variable_naming_a_folder_without_env_is_ignored()
        {
            var empty = Deploy("empty", withEnv: false);
            var main = Deploy("main");
            var found = LocalStack.ResolveDeployFolder(Start("wt"), _ => empty, _ => Path.Combine(_root, "main", ".git") + Path.DirectorySeparatorChar);
            Assert.Equal(main, found);
        }

        [Fact]
        public void The_main_worktree_is_used_last()
        {
            var main = Deploy("main");
            var found = LocalStack.ResolveDeployFolder(Start("wt"), _ => null, _ => Path.Combine(_root, "main", ".git"));
            Assert.Equal(main, found);
        }

        [Fact]
        public void Nothing_found_is_null_so_CI_skips()
        {
            Assert.Null(LocalStack.ResolveDeployFolder(Start("wt"), _ => null, _ => null));
            Assert.Null(LocalStack.ResolveDeployFolder(Start("wt"), _ => null, _ => Path.Combine(_root, "nowhere", ".git")));
        }
    }
}
