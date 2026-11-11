using Juice.Notifications.Api;
using Juice.Notifications.Data.PostgreSQL;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddJuiceNotifications(builder.Configuration).AddPostgresClaimStore();

var app = builder.Build();
app.UseJuiceNotifications();
app.MapJuiceNotifications();
app.Run();

/// <summary>The host's entry point, visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;
