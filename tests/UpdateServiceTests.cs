using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using UsageTree;

// Compile with UPDATE_TESTS. Only the test assembly substitutes the Windows resolver and
// maps the official HTTPS names to non-resolvable HTTP fixtures served by a loopback proxy.
internal static class UpdateServiceTests
{
    const string Origin = "https://github.com/peterwangze/windows_disk_manager";
    const string Cdn = "https://release-assets.githubusercontent.com/test/file";
    static readonly byte[] Payload = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location);
    static readonly CancellationToken Token = CancellationToken.None;
    static string metadata;
    static string mode;

    static string Release(string version)
    {
        string tag = "v" + version, name = "WindowsDiskManager-" + version + "-win-x64-Setup.exe";
        string hash;
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Payload)).Replace("-", "");
        return new JavaScriptSerializer().Serialize(new {
            tag_name = tag, html_url = Origin + "/releases/tag/" + tag, published_at = "2026-01-01T00:00:00Z", draft = false, prerelease = false,
            assets = new[] { new { name = name, browser_download_url = Origin + "/releases/download/" + tag + "/" + name,
                size = Payload.Length, digest = "sha256:" + hash, state = "uploaded" } }
        });
    }

    static void Assert(bool ok, string assertion) { if (!ok) throw new Exception(assertion); }
    static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    sealed class Recorder : IDisposable
    {
        readonly TcpListener server = new TcpListener(IPAddress.Loopback, 0);
        readonly Thread worker;
        public readonly List<string> Requests = new List<string>();
        public readonly ManualResetEvent Holding = new ManualResetEvent(false);
        public readonly ManualResetEvent ReleaseResponse = new ManualResetEvent(false);
        public readonly Uri Uri;
        public Recorder()
        {
            server.Start();
            Uri = new Uri("http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port);
            worker = new Thread(Serve) { IsBackground = true };
            worker.Start();
        }
        void Serve()
        {
            try
            {
                while (true)
                {
                    using (var client = server.AcceptTcpClient())
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        string first = reader.ReadLine();
                        if (String.IsNullOrEmpty(first)) continue;
                        string line;
                        while (!String.IsNullOrEmpty(line = reader.ReadLine()))
                            Assert(!line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase), "Origin credentials sent.");
                        Uri target = new Uri(first.Split(' ')[1]);
                        lock (Requests) Requests.Add(target.Host);
                        if (mode == "hold-cdn" && target.Host == "release-assets.githubusercontent.com.fixture.invalid")
                        {
                            Holding.Set();
                            ReleaseResponse.WaitOne(10000);
                            continue;
                        }
                        string status = "200 OK", header = "";
                        byte[] body = new byte[0];
                        if (mode == "denied") status = "407 Proxy Authentication Required";
                        else if (target.Host == "api.github.com.fixture.invalid") body = Encoding.UTF8.GetBytes(metadata);
                        else if (target.Host == "github.com.fixture.invalid")
                        {
                            status = "302 Found";
                            header = "Location: " + (mode == "bad-location" ? "http://github.com/file" : Cdn) + "\r\n";
                        }
                        else if (target.Host == "release-assets.githubusercontent.com.fixture.invalid")
                        {
                            body = (byte[])Payload.Clone();
                            if (mode == "bad-content") body[0] ^= 1;
                            if (mode == "short") Array.Resize(ref body, body.Length - 1);
                        }
                        else status = "400 Bad Request";
                        byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n" + header +
                            "Connection: close\r\nContent-Length: " + body.Length + "\r\n\r\n");
                        stream.Write(headers, 0, headers.Length);
                        stream.Write(body, 0, body.Length);
                    }
                }
            }
            catch (SocketException) { }
        }
        public void Dispose()
        {
            ReleaseResponse.Set(); server.Stop(); worker.Join(3000);
            Holding.Dispose(); ReleaseResponse.Dispose();
        }
    }

    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--launch-probe")
        {
            using (var signal = EventWaitHandle.OpenExisting(args[1])) signal.Set();
            return;
        }
        metadata = Release("8.5.0");
        var current = new Version(8, 5, 0, 0);
        Assert(!UpdateService.ParseRelease(metadata, current).IsNewer, "Equivalent 3/4-part versions");
        Assert(!UpdateService.ParseRelease(metadata, new Version(8, 5, 0, 1)).IsNewer, "Downgrade");
        Assert(UpdateService.ParseRelease(Release("8.5.0.1"), current).IsNewer, "Fourth version component");
        Assert(UpdateService.ParseRelease(Release("8.10.0"), current).IsNewer, "Numeric comparison");
        foreach (string broken in new[] { metadata.Replace("sha256:", "md5:"), metadata.Replace("\"uploaded\"", "\"new\""),
            metadata.Replace("\"prerelease\":false", "\"prerelease\":true"), metadata.Replace("\"draft\":false", "\"draft\":true"),
            metadata.Replace("/releases/download/", "/different/"), metadata.Replace("v8.5.0", "v8.5.0-beta"),
            metadata.Replace("win-x64-Setup.exe", "portable.zip") })
            Throws<InvalidDataException>(() => UpdateService.ParseRelease(broken, new Version(1, 0)));
        int resolutions = 0;
        string staged = null;
        using (var proxy = new Recorder())
        {
            UpdateService.TestProxy = delegate { Interlocked.Increment(ref resolutions); return new WebProxy(proxy.Uri, false); };
            UpdateService.TestRequest = address => (HttpWebRequest)WebRequest.Create(
                new Uri("http://" + address.Host + ".fixture.invalid" + address.PathAndQuery));
            try
            {
                var release = UpdateService.CheckAsync(Token).GetAwaiter().GetResult();
                staged = UpdateService.DownloadAsync(release, null, Token).GetAwaiter().GetResult();
                UpdateService.VerifyInstaller(release, staged);
                Assert(File.ReadAllBytes(staged).SequenceEqual(Payload), "Streamed bytes");
                Assert(proxy.Requests.SequenceEqual(new[] { "api.github.com.fixture.invalid", "github.com.fixture.invalid",
                    "release-assets.githubusercontent.com.fixture.invalid" }) && resolutions == 3, "Proxy used for each hop");
                Console.WriteLine("Recording proxy: metadata -> asset -> redirected CDN, three proxy resolutions");
                string eventName = @"Local\WdmUpdateLaunch-" + Guid.NewGuid().ToString("N");
                using (var signal = new EventWaitHandle(false, EventResetMode.ManualReset, eventName))
                {
                    UpdateService.StartInstaller(release, staged, "--launch-probe " + eventName);
                    Assert(signal.WaitOne(10000), "Verified executable did not start");
                }
                // The signal is sent just before the child exits. Wait for its image mapping to close.
                for (int attempt = 0; ; attempt++)
                {
                    try { File.WriteAllBytes(staged, Enumerable.Repeat((byte)1, Payload.Length).ToArray()); break; }
                    catch (IOException) { if (attempt >= 100) throw; Thread.Sleep(40); }
                }
                Throws<InvalidDataException>(() => UpdateService.VerifyInstaller(release, staged));
                Throws<InvalidDataException>(() => UpdateService.StartInstaller(release, staged, ""));
                foreach (string error in new[] { "bad-location", "bad-content", "short" })
                {
                    mode = error;
                    Throws<InvalidDataException>(() => UpdateService.DownloadAsync(release, null, Token).GetAwaiter().GetResult());
                }
                mode = "denied";
                int before = resolutions;
                var checkFailure = Throws<IOException>(() => UpdateService.CheckAsync(Token).GetAwaiter().GetResult());
                var assetFailure = Throws<IOException>(() => UpdateService.DownloadAsync(release, null, Token).GetAwaiter().GetResult());
                Assert(checkFailure.Message.Contains("代理") && assetFailure.Message.Contains("代理") &&
                    resolutions == before + 2 && proxy.Requests.Count == resolutions, "Proxy denials must propagate without fallback");
                using (var cancel = new CancellationTokenSource())
                {
                    cancel.Cancel();
                    Throws<OperationCanceledException>(() => UpdateService.CheckAsync(cancel.Token).GetAwaiter().GetResult());
                    Throws<OperationCanceledException>(() => UpdateService.DownloadAsync(release, null, cancel.Token).GetAwaiter().GetResult());
                }
                mode = "hold-cdn";
                using (var cancel = new CancellationTokenSource())
                {
                    var download = UpdateService.DownloadAsync(release, null, cancel.Token);
                    Assert(proxy.Holding.WaitOne(10000), "Transfer did not reach stalled CDN");
                    cancel.Cancel();
                    Throws<OperationCanceledException>(() => download.GetAwaiter().GetResult());
                    proxy.ReleaseResponse.Set();
                }
                Console.WriteLine("PASS: versions, stable metadata, exact asset, hash/length, bad redirect, cancellation, 407 for check/download, locked prelaunch validation.");
            }
            finally { if (staged != null) UpdateService.DiscardInstaller(staged); }
        }
    }
}
