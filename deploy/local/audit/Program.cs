// The local stack's audit service (docker-compose.yml, service "audit"): receives the notification service's audit records over gRPC and stores them
// in the database named by ConnectionStrings:PostgreConnection, schema "audit". Read them with psql (deploy/local/README.md).
using Juice.Audit.AspNetCore.Extensions;
using Juice.Audit.EF;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureAuditGrpcHost(builder.Configuration, db =>
{
    db.DatabaseProvider = "PostgreSQL";
    db.Schema = "audit";
});
builder.Services.AddMediatR(options => options.RegisterServicesFromAssemblyContaining<Program>());
builder.Services.AddGrpc();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.MigrateAsync();
}
app.MapAuditGrpcServer();
app.MapGet("/health", () => "ok");
app.Run();
