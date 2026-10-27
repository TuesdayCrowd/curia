using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Curia.Client.Tests;

/// <summary>
/// A Forum that is only a recorder: a loopback listener that writes down every request line it receives and answers each with a fixed
/// document (a token for the token endpoint, the Forum's "No such post" problem document for everything else). A probe of the form
/// "nothing was sent" reads <see cref="Requests"/>; the answer is incidental. Used with <see cref="CuriaCli"/> because the command
/// builds its own client from the profile, so there is no handler to inject (R11.58, R11.57).
/// </summary>
internal sealed class RecordingForum : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;

    internal RecordingForum()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        Address = new Uri($"http://127.0.0.1:{port}");
        _listener.Prefixes.Add(Address.ToString());
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    internal Uri Address { get; }

    /// <summary>Every request received, as "METHOD /path?query", in order. The query is as sent, still percent-encoded.</summary>
    internal ConcurrentQueue<string> Requests { get; } = new();

    internal string[] Lines => [.. Requests];

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            var raw = context.Request.RawUrl ?? string.Empty;
            Requests.Enqueue($"{context.Request.HttpMethod} {raw}");

            var (status, body, type) = (context.Request.Url?.AbsolutePath ?? string.Empty) == "/oauth/token"
                ? (200, """{"access_token":"recorder-token","token_type":"DPoP","expires_in":300,"scope":"question:create"}""", "application/json")
                : (404, """{"type":"curia/posts/not-found","title":"No such post","detail":"post=recorded"}""", "application/problem+json");

            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = type;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The listener was closed under the loop; that is how it stops.
        }
    }
}

/// <summary>Runs the real <c>curia</c> command in a child process with a profile store of its own.</summary>
internal static class CuriaCli
{
    internal static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string home, CancellationToken ct, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "curia.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["CURIA_CLIENT_HOME"] = home;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>A fresh profile directory, with one agent named <c>alice</c> enrolled against <paramref name="forum"/> when it is given.</summary>
    internal static string NewHome(Uri? forum)
    {
        var home = Directory.CreateTempSubdirectory("curia-cli-probe-").FullName;
        if (forum is not null)
        {
            using var agent = Enroll(home, forum);
        }

        return home;
    }

    private static EnrolledAgent Enroll(string home, Uri forum)
    {
        var created = new ProfileStore(home).Create("alice", "https://agents.example/alice", "alice-1", forum);
        if (!created.TryGetValue(out var agent, out var error))
            throw new InvalidOperationException($"{error!.Type}: {error.Title}");

        return agent!;
    }
}
