using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Curia.Infrastructure.Tests.Embedder;

/// <summary>
/// Errata G65 (D10a), Amendment 2 ruling 42 (SEM51), model-free and loopback only: <c>tools/model/fetch-model.py</c> over a response that is cut short. A truncated body is an
/// <c>http.client.IncompleteRead</c>, which is not an <c>OSError</c>, so a script that catches only <c>URLError</c>, <c>OSError</c> and <c>TimeoutError</c> aborts with a traceback and never tries
/// the second source.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FetchModelScriptTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("a vocabulary line\n", 300)));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// A listener on loopback: <c>/broken</c> opens a chunk of 4096 bytes, sends ten and closes (a chunked body cut short is what raises <c>IncompleteRead</c>: a body with a
    /// <c>Content-Length</c> that falls short reads as an end of file, which the script already reports as a digest mismatch); <c>/good</c> serves the payload.
    /// </summary>
    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        internal Server()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => LoopAsync(_stop.Token));
        }

        internal int Port { get; }

        private async Task LoopAsync(CancellationToken stop)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(stop);
                    _ = Task.Run(() => ServeAsync(client, stop), stop);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // the test ended
            }
        }

        private static async Task ServeAsync(TcpClient client, CancellationToken stop)
        {
            using (client)
            {
                var stream = client.GetStream();
                var request = new StringBuilder();
                var buffer = new byte[1024];
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, stop);
                    if (read == 0) return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var path = request.ToString().Split(' ')[1];
                if (path == "/broken")
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n1000\r\n"), stop);
                    await stream.WriteAsync(Payload.AsMemory(0, 10), stop);
                    await stream.FlushAsync(stop);
                    return;
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {Payload.Length}\r\nConnection: close\r\n\r\n"), stop);
                await stream.WriteAsync(Payload, stop);
                await stream.FlushAsync(stop);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
            _stop.Dispose();
        }
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string manifest, string output)
    {
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(Root(), "tools", "model", "fetch-model.py"));
        start.ArgumentList.Add("--out");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("--manifest");
        start.ArgumentList.Add(manifest);
        start.ArgumentList.Add("--no-cache");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("python3 did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string WriteManifest(string directory, params string[] sources)
    {
        var manifest = new
        {
            files = new Dictionary<string, object>
            {
                ["vocab.txt"] = new { bytes = Payload.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(Payload)), sources },
            },
        };
        var path = Path.Combine(directory, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    /// <summary>
    /// SEM51. Case one, both sources (the broken one first): exit 0, the file equals the bytes, no <c>Traceback</c> on stderr. Case two, the broken source only: exit 1, stderr names the file and
    /// <c>could not fetch</c>, no <c>Traceback</c>, and no <c>.part</c> file is left. Predicted red on <c>a0cda877</c>: <c>Traceback ... http.client.IncompleteRead</c>.
    /// </summary>
    [Fact]
    public async Task R11_91_SEM51_ATruncatedResponseIsASourceThatFailedAndTheNextSourceIsTried()
    {
        using var server = new Server();
        var broken = $"http://127.0.0.1:{server.Port}/broken";
        var good = $"http://127.0.0.1:{server.Port}/good";
        var work = Directory.CreateTempSubdirectory("curia-sem51-").FullName;
        try
        {
            var both = Path.Combine(work, "both");
            Directory.CreateDirectory(both);
            var (exitBoth, _, errBoth) = await RunAsync(WriteManifest(work, broken, good), both);
            Assert.True(exitBoth == 0, $"both sources: exit {exitBoth}; stderr: {errBoth.Trim()}");
            Assert.DoesNotContain("Traceback", errBoth, StringComparison.Ordinal);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(Path.Combine(both, "vocab.txt"), Ct));

            var only = Path.Combine(work, "only");
            Directory.CreateDirectory(only);
            var (exitOnly, _, errOnly) = await RunAsync(WriteManifest(work, broken), only);
            Assert.True(exitOnly == 1, $"the broken source only: exit {exitOnly}; stderr: {errOnly.Trim()}");
            Assert.DoesNotContain("Traceback", errOnly, StringComparison.Ordinal);
            Assert.Contains("vocab.txt", errOnly, StringComparison.Ordinal);
            Assert.Contains("could not fetch", errOnly, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(only, "*.part"));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
