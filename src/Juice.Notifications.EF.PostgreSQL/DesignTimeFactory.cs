using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Juice.Notifications.EF.PostgreSQL
{
    /// <summary>For <c>dotnet ef migrations add</c>: builds the model only, never connects.</summary>
    public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
    {
        public NotificationsDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<NotificationsDbContext>();
            NotificationsDbContext.Configure(options, "Host=localhost;Database=notifications_design");
            return new NotificationsDbContext(options.Options);
        }
    }
}
