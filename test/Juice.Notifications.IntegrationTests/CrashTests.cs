using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Juice.Notifications.Api.Contracts;
using Microsoft.EntityFrameworkCore;
using static Juice.Notifications.IntegrationTests.DeliveryTests;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Proof 1 (phase-1-plan §6, M9): an accepted request survives a crash right after <c>202</c>. The real host runs as a child process
    /// (Juice.Notifications.App), first with its workers off; it is killed (whole process tree, no graceful stop) as soon as it has answered
    /// <c>202</c>; a new host process with workers then delivers the request to the receiver.
    /// </summary>
    public sealed class CrashTests : IAsyncLifetime
    {
        private readonly ScratchDatabase _database = new();

        public ValueTask InitializeAsync() => _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        [Fact]
        public async Task Accepted_request_is_delivered_after_the_host_is_killed_right_after_202()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            var tenant = Guid.CreateVersion7();
            Guid id;

            await using (var first = await ChildHost.StartAsync(_database.ConnectionString, runStages: false))
            {
                using var admin = first.Client(await LocalStack.AdminTokenAsync());
                using var producer = first.Client(await LocalStack.ProducerTokenAsync());
                await Ok(admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody("crash", "Crash", "sample-producer"), Ct));
                await Ok(admin.PutAsync($"/api/v1/applications/crash/tenants/{tenant}", null, Ct));
                var t = $"/api/v1/tenants/{tenant}";
                await Ok(admin.PutAsJsonAsync($"{t}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port]), Ct));
                await Ok(admin.PutAsJsonAsync($"{t}/integrations/hooks", new PutIntegrationBody("webhook", "webhook", null, TestHooks.SecretRef, null, null), Ct));
                await Ok(admin.PutAsJsonAsync($"{t}/integrations/hooks/targets/receiver", new PutTargetBody(receiver.Url, null, null), Ct));
                await Ok(admin.PutAsJsonAsync($"{t}/routes/all", new PutRouteBody(DeliveryTests.Json("""{ "type": "crash.*" }"""), ["receiver"], null), Ct));
                await Ok(producer.PutAsJsonAsync("/api/v1/applications/crash/types", new DeclareTypeBody("crash.happened", "Crash", "alerts", null, null,
                    null, null, DeliveryTests.Json("""{ "type": "object" }"""), null, null, null, DeliveryTests.Json("""{ "webhook": "passthrough" }""")), Ct));

                var response = await producer.PostAsJsonAsync($"{t}/notifications", new SendNotificationBody("crash.happened", null, "crash-1", null, null, null,
                    null, null, DeliveryTests.Json("""{ "at": "now" }"""), null, null), Ct);
                // Killed the moment the answer is in: no graceful stop, nothing flushed after the 202.
                first.Kill();
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                id = (await response.Content.ReadFromJsonAsync<AcceptedResponse>(Ct))!.Id;
            }

            await using (var db = _database.Context())
            {
                Assert.True(await db.Requests.AnyAsync(r => r.Id == id, Ct), "the accepted request was stored before the 202");
                Assert.Empty(await db.Deliveries.ToListAsync(Ct)); // nothing ran on the killed host
            }
            Assert.Empty(receiver.Requests);

            await using (var second = await ChildHost.StartAsync(_database.ConnectionString, runStages: true))
            {
                // Until the delivery is saved as delivered (the receiver hears of it a moment before the dispatcher saves it).
                for (var i = 0; i < 300; i++)
                {
                    await using var db = _database.Context();
                    if (await db.Deliveries.AnyAsync(d => d.Status == DeliveryStatus.Delivered, Ct))
                    {
                        break;
                    }
                    await Task.Delay(100, Ct);
                }
            }
            var received = Assert.Single(receiver.Requests);
            await using (var db = _database.Context())
            {
                var delivery = await db.Deliveries.SingleAsync(Ct);
                Assert.Equal((DeliveryStatus.Delivered, delivery.Id.ToString()), (delivery.Status, received.Id));
                Assert.Contains(id.ToString(), received.Body);
            }
        }

        /// <summary>The real host as a process of its own, on a free port, against the given database.</summary>
        private sealed class ChildHost : IAsyncDisposable
        {
            private readonly Process _process;
            private readonly Uri _address;

            private ChildHost(Process process, Uri address)
            {
                _process = process;
                _address = address;
            }

            /// <summary>
            /// Starts the host and waits until it is ready. A port taken between choosing it and the child binding it makes the child exit at
            /// once: then another port is tried.
            /// </summary>
            public static async Task<ChildHost> StartAsync(string connectionString, bool runStages)
            {
                for (var tries = 1; ; tries++)
                {
                    var host = Launch(connectionString, runStages);
                    try
                    {
                        if (await host.ReadyAsync())
                        {
                            return host;
                        }
                    }
                    catch
                    {
                        // Cancelled, timed out, or anything else: never leave the child running.
                        await host.DisposeAsync();
                        throw;
                    }
                    var exitCode = host._process.ExitCode;
                    await host.DisposeAsync();
                    Assert.True(tries < 3, $"the host exited with {exitCode}, {tries} times");
                }
            }

            /// <summary>True once ready; false if the process exited first (a port taken by someone else); throws if it never becomes ready.</summary>
            private async Task<bool> ReadyAsync()
            {
                using var probe = new HttpClient { BaseAddress = _address, Timeout = TimeSpan.FromSeconds(2) };
                for (var i = 0; i < 300; i++)
                {
                    if (_process.HasExited)
                    {
                        return false;
                    }
                    try
                    {
                        if ((await probe.GetAsync("/health/ready", Ct)).IsSuccessStatusCode)
                        {
                            return true;
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !Ct.IsCancellationRequested))
                    {
                    }
                    await Task.Delay(100, Ct);
                }
                throw new TimeoutException("The host never became ready.");
            }

            private static ChildHost Launch(string connectionString, bool runStages)
            {
                var app = AppPath();
                Assert.SkipWhen(app is null, "Juice.Notifications.App isn't built next to the tests");
                var port = FreePort();
                var address = new Uri($"http://127.0.0.1:{port}");
                var start = new ProcessStartInfo("dotnet", [app!])
                {
                    WorkingDirectory = Path.GetDirectoryName(app)!,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Environment =
                    {
                        ["ASPNETCORE_URLS"] = address.ToString(),
                        ["ASPNETCORE_ENVIRONMENT"] = "Development",
                        ["ConnectionStrings__Notifications"] = connectionString,
                        ["Database__Migrate"] = "true",
                        ["Auth__Authority"] = LocalStack.KeycloakRealm,
                        ["Auth__RequireHttpsMetadata"] = "false",
                        ["Delivery__AllowHttpToAllowlisted"] = "true",
                        ["Stages__Run"] = runStages ? "true" : "false",
                        ["Stages__Planner__Poll"] = "00:00:00.200",
                        ["Stages__DeliveryPlanner__Poll"] = "00:00:00.200",
                        ["Stages__Dispatcher__Poll"] = "00:00:00.200",
                    },
                };
                var process = Process.Start(start)!;
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var host = new ChildHost(process, address);
                // If the test runner itself goes away, the child goes with it.
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Already gone and disposed.
                    }
                };
                return host;
            }

            public HttpClient Client(string token)
            {
                var client = new HttpClient { BaseAddress = _address };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return client;
            }

            /// <summary>No graceful stop: the process tree ends now, as a crash or an OOM kill would end it.</summary>
            public void Kill()
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }

            public ValueTask DisposeAsync()
            {
                if (!_process.HasExited)
                {
                    Kill();
                }
                _process.Dispose();
                return ValueTask.CompletedTask;
            }

            /// <summary>The App's own build output, next to this test project's (build/bin/{project}/{configuration}/{framework}).</summary>
            private static string? AppPath()
            {
                var tests = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
                var framework = tests.Name;
                var configuration = tests.Parent!.Name;
                var bin = tests.Parent!.Parent!.Parent!.FullName;
                var path = Path.Combine(bin, "Juice.Notifications.App", configuration, framework, "Juice.Notifications.App.dll");
                return File.Exists(path) ? path : null;
            }

            private static int FreePort()
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
        }
    }
}
