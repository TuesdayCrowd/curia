using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// A Forum on a real loopback socket for the facts that run the real <c>curia</c> command in a child process (errata G49, SEB40, SEB41, SEB44): it answers
/// the token endpoint, records every request as "METHOD path", and answers every other path as the fact tells it to. No DPoP proof is checked: the facts
/// are about what the command asks for and prints, and the Forum's own side of the protocol is the Api suite's.
/// </summary>
internal sealed class ListenerForum : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _serving;

    internal ListenerForum()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Address = new Uri($"http://127.0.0.1:{port}");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    internal Uri Address { get; }

    /// <summary>Every request answered, in order, as "METHOD path".</summary>
    internal ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>The status and body for a method and path that is not the token endpoint.</summary>
    internal Func<string, string, (int Status, string Body)> Answer { get; set; } = (_, _) => (404, """{"type":"curia/request/no-route","title":"No route","detail":null}""");

    internal IReadOnlyList<string> Calls(string request) => [.. Requests.Where(r => r == request)];

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            var request = $"{context.Request.HttpMethod} {context.Request.Url!.AbsolutePath}";
            Requests.Enqueue(request);
            var (status, body) = context.Request.Url.AbsolutePath == "/oauth/token"
                ? (200, """{"access_token":"stub-access-token","token_type":"DPoP","expires_in":300,"scope":"question:create"}""")
                : Answer(context.Request.HttpMethod, context.Request.Url.AbsolutePath);
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = status is 200 or 201 ? "application/json" : "application/problem+json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Close();
        _serving.Wait(TimeSpan.FromSeconds(5));
        GC.SuppressFinalize(this);
    }
}

/// <summary>A profile store under a temporary home and the means to run the real <c>curia</c> command against it.</summary>
internal sealed class CliHome : IDisposable
{
    internal CliHome(Uri? forum = null)
    {
        Path = Directory.CreateTempSubdirectory("curia-budget-cli-").FullName;
        Assert.True(new ProfileStore(Path).Create("alice", "https://agents.example/alice", "alice-1", forum ?? new Uri("http://forum.test")).TryGetValue(out var agent, out var error), error?.Type);
        agent!.Dispose();
    }

    internal string Path { get; }

    internal async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var ct = TestContext.Current.CancellationToken;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "curia.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["CURIA_CLIENT_HOME"] = Path;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout, await stderr);
    }

    public void Dispose()
    {
        Directory.Delete(Path, recursive: true);
        GC.SuppressFinalize(this);
    }
}
