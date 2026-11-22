using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageTree
{
    public sealed class UpdateRelease
    {
        public Version Version { get; private set; }
        public string VersionText { get; private set; }
        public bool IsNewer { get; private set; }
        public string AssetName { get; private set; }
        public long Size { get; private set; }
        internal Uri DownloadUri { get; private set; }
        internal string Digest { get; private set; }

        internal UpdateRelease(Version version, string text, bool newer, string name, long size, Uri uri, string digest)
        {
            Version = version; VersionText = text; IsNewer = newer;
            AssetName = name; Size = size; DownloadUri = uri; Digest = digest;
        }
    }

    public static class UpdateService
    {
        const string Repo = "peterwangze/windows_disk_manager";
        const string WebRoot = "https://github.com/" + Repo;
        const int MetadataLimit = 2 * 1024 * 1024;
        static readonly Uri Latest = new Uri("https://api.github.com/repos/" + Repo + "/releases/latest");
        static readonly Regex TagPattern = new Regex(@"\Av?(?<version>(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*)){2,3})\z", RegexOptions.CultureInvariant);
        static readonly Regex DigestPattern = new Regex(@"\Asha256:[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);
        static readonly string UpdateCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsDiskManager", "Updates");
        static Version CurrentVersion { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

        public static Task<UpdateRelease> CheckAsync(CancellationToken token)
        {
            return Task.Run(() => Request(Latest, null, token, delegate(HttpWebResponse response, CancellationToken active)
            {
                if (response.ContentLength > MetadataLimit) throw new InvalidDataException("更新信息过大。");
                using (var source = response.GetResponseStream())
                using (var data = new MemoryStream())
                {
                    byte[] buffer = new byte[16384];
                    int count;
                    while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        active.ThrowIfCancellationRequested();
                        if (data.Length + count > MetadataLimit) throw new InvalidDataException("更新信息过大。");
                        data.Write(buffer, 0, count);
                    }
                    return ParseRelease(new UTF8Encoding(false, true).GetString(data.ToArray()), CurrentVersion);
                }
            }), token);
        }

        static Version FourParts(Version version)
        {
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }

        internal static UpdateRelease ParseRelease(string json, Version installed)
        {
            var reader = new JavaScriptSerializer { MaxJsonLength = MetadataLimit, RecursionLimit = 32 };
            var doc = reader.DeserializeObject(json) as Dictionary<string, object>;
            if (doc == null || !Object.Equals(Field(doc, "draft"), false) || !Object.Equals(Field(doc, "prerelease"), false))
                throw new InvalidDataException("GitHub 返回的版本不是已发布的稳定版。");
            string tag = Field(doc, "tag_name") as string;
            Match match = TagPattern.Match(tag ?? "");
            string text = match.Groups["version"].Value;
            Version version;
            DateTimeOffset published;
            if (!match.Success || !Version.TryParse(text, out version) ||
                !DateTimeOffset.TryParse(Field(doc, "published_at") as string, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out published) ||
                !String.Equals(Field(doc, "html_url") as string, WebRoot + "/releases/tag/" + tag, StringComparison.Ordinal))
                throw new InvalidDataException("无法验证发布仓库、版本号或发布时间。");
            version = FourParts(version);
            if (version <= FourParts(installed)) return new UpdateRelease(version, text, false, null, 0, null, null);

            string name = "WindowsDiskManager-" + text + "-win-x64-Setup.exe";
            var assets = Field(doc, "assets") as object[];
            var matches = (assets ?? new object[0]).OfType<Dictionary<string, object>>()
                .Where(asset => Object.Equals(Field(asset, "name"), name)).Take(2).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("该版本缺少唯一的 Windows x64 安装包。");
            var file = matches[0];
            string digest = Field(file, "digest") as string;
            string expected = WebRoot + "/releases/download/" + tag + "/" + name;
            object rawSize = Field(file, "size");
            long size = rawSize is int || rawSize is long ? Convert.ToInt64(rawSize, CultureInfo.InvariantCulture) : 0;
            if (!Object.Equals(Field(file, "state"), "uploaded") || !Object.Equals(Field(file, "browser_download_url"), expected) ||
                size <= 0 || digest == null || !DigestPattern.IsMatch(digest))
                throw new InvalidDataException("安装包的下载地址、大小或 SHA-256 校验信息无效。");
            return new UpdateRelease(version, text, true, name, size, new Uri(expected), digest.Substring(7));
        }

        static object Field(Dictionary<string, object> data, string key)
        {
            object value; return data.TryGetValue(key, out value) ? value : null;
        }

        public static Task<string> DownloadAsync(UpdateRelease release, IProgress<int> progress, CancellationToken token)
        {
            return Task.Run(delegate
            {
                EnsureNewer(release);
                string folder = CreateStagingDirectory();
                string path = Path.Combine(folder, release.AssetName);
                string partial = path + ".partial";
                try
                {
                    Request(release.DownloadUri, release, token, delegate(HttpWebResponse response, CancellationToken active)
                    {
                        if (response.ContentLength >= 0 && response.ContentLength != release.Size)
                            throw new InvalidDataException("服务器返回的安装包长度与发布信息不符。");
                        using (var stream = response.GetResponseStream())
                        using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
                            CopyAndCheck(stream, output, release, progress, active);
                        return true;
                    });
                    token.ThrowIfCancellationRequested();
                    File.Move(partial, path);
                    if (progress != null) progress.Report(100);
                    return path;
                }
                catch { TryDelete(partial); DiscardInstaller(path); throw; }
            }, token);
        }

        static void CopyAndCheck(Stream source, Stream destination, UpdateRelease release, IProgress<int> progress, CancellationToken token)
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int last = -1;
            using (var sha = SHA256.Create())
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    if (read > release.Size - total) throw new InvalidDataException("下载的安装包大于发布文件。");
                    total += read;
                    if (destination != null) destination.Write(buffer, 0, read);
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                    int percent = Math.Min(99, (int)(100.0 * total / release.Size));
                    if (progress != null && percent != last) { progress.Report(percent); last = percent; }
                }
                token.ThrowIfCancellationRequested();
                sha.TransformFinalBlock(new byte[0], 0, 0);
                if (total != release.Size || !String.Equals(BitConverter.ToString(sha.Hash).Replace("-", ""), release.Digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包的大小或 SHA-256 校验失败，请重新下载。");
            }
        }

        // All hops, including CDN redirects, get their own Windows proxy resolver. Its PAC,
        // configured exceptions and direct routing decisions stay in effect for that URI.
        static T Request<T>(Uri address, UpdateRelease release, CancellationToken token, Func<HttpWebResponse, CancellationToken, T> consume)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(release == null ? TimeSpan.FromSeconds(45) : TimeSpan.FromMinutes(20));
                try
                {
                    for (int hop = 0; hop <= 5; hop++)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (!AllowedAddress(address, release)) throw new InvalidDataException("已拒绝非官方更新地址。");
#if UPDATE_TESTS
                        var request = TestRequest(address);
                        var proxy = TestProxy();
#else
                        var request = (HttpWebRequest)WebRequest.Create(address);
                        var proxy = WebRequest.GetSystemWebProxy();
#endif
                        proxy.Credentials = CredentialCache.DefaultNetworkCredentials;
                        request.Proxy = proxy;
                        request.Credentials = null;
                        request.AllowAutoRedirect = false;
                        request.Timeout = 45000;
                        request.ReadWriteTimeout = 45000;
                        request.UserAgent = "WindowsDiskManager/" + CurrentVersion.ToString(3);
                        request.Accept = release == null ? "application/vnd.github+json" : "application/octet-stream";
                        if (release == null) request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
                        using (deadline.Token.Register(request.Abort))
                        using (var response = (HttpWebResponse)request.GetResponse())
                        {
                            int code = (int)response.StatusCode;
                            if (code == 200) return consume(response, deadline.Token);
                            if (code != 301 && code != 302 && code != 303 && code != 307 && code != 308)
                                throw new IOException("更新服务器返回 HTTP " + code + "。");
                            Uri next;
                            string location = response.Headers[HttpResponseHeader.Location];
                            if (String.IsNullOrWhiteSpace(location) || !Uri.TryCreate(address, location, out next))
                                throw new InvalidDataException("更新服务器返回了无效的重定向。");
                            address = next;
                        }
                    }
                    throw new IOException("更新服务器的重定向次数过多。");
                }
                catch (Exception ex)
                {
                    var web = ex as WebException;
                    var failed = web == null ? null : web.Response as HttpWebResponse;
                    int code = failed == null ? 0 : (int)failed.StatusCode;
                    if (failed != null) failed.Dispose();
                    token.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested || (web != null && web.Status == WebExceptionStatus.Timeout))
                        throw new TimeoutException("连接更新服务器或读取安装包超时。", ex);
                    if (code == 407) throw new IOException("Windows 系统代理需要身份验证。", ex);
                    if (code == 403 || code == 429) throw new IOException("GitHub 拒绝了请求或已达到访问频率限制（HTTP " + code + "）。", ex);
                    if (web != null) throw new IOException("通过 Windows 系统代理访问更新服务器失败。" + web.Message, ex);
                    throw;
                }
            }
        }
#if UPDATE_TESTS
        internal static Func<Uri, HttpWebRequest> TestRequest;
        internal static Func<IWebProxy> TestProxy;
#endif

        internal static bool AllowedAddress(Uri uri, UpdateRelease release)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
            if (release == null) return Uri.Compare(uri, Latest, UriComponents.AbsoluteUri, UriFormat.UriEscaped, StringComparison.Ordinal) == 0;
            return uri == release.DownloadUri || uri.Host == "release-assets.githubusercontent.com" ||
                uri.Host == "objects.githubusercontent.com" || uri.Host == "github-releases.githubusercontent.com";
        }

        static void EnsureNewer(UpdateRelease release)
        {
            if (release == null || !release.IsNewer || release.Version <= FourParts(CurrentVersion) || release.DownloadUri == null)
                throw new InvalidOperationException("仅可安装比当前程序更新的稳定版本。");
        }

        static string CreateStagingDirectory()
        {
            Directory.CreateDirectory(UpdateCache);
            CheckDirectory(UpdateCache);
            string folder = Path.Combine(UpdateCache, Guid.NewGuid().ToString("N"));
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            using (var identity = WindowsIdentity.GetCurrent())
                acl.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(folder, acl);
            CheckDirectory(folder);
            return folder;
        }

        static void CheckDirectory(string folder)
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新暂存目录不能为链接。");
        }

        static bool StagedPath(string path)
        {
            if (String.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !String.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return false;
            string parent = Path.GetDirectoryName(path);
            if (parent == null || !String.Equals(Path.GetDirectoryName(parent), UpdateCache, StringComparison.OrdinalIgnoreCase)) return false;
            Guid id;
            return Guid.TryParseExact(Path.GetFileName(parent), "N", out id) &&
                Regex.IsMatch(Path.GetFileName(path), @"\AWindowsDiskManager-[0-9]+(\.[0-9]+){2,3}-win-x64-Setup\.exe\z", RegexOptions.CultureInvariant);
        }

        static FileStream OpenVerified(UpdateRelease release, string path)
        {
            EnsureNewer(release);
            if (!StagedPath(path) || !String.Equals(Path.GetFileName(path), release.AssetName, StringComparison.Ordinal))
                throw new InvalidDataException("安装包不在本次更新的暂存目录中。");
            CheckDirectory(UpdateCache);
            CheckDirectory(Path.GetDirectoryName(path));
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("安装包不能为链接。");
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { CopyAndCheck(file, null, release, null, CancellationToken.None); return file; }
            catch { file.Dispose(); throw; }
        }

        public static void VerifyInstaller(UpdateRelease release, string path)
        {
            using (OpenVerified(release, path)) { }
        }

        public static void StartInstaller(UpdateRelease release, string path, string arguments)
        {
            using (OpenVerified(release, path))
            using (var process = Process.Start(new ProcessStartInfo(path, arguments ?? "") {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)
            })) { if (process == null) throw new IOException("无法启动更新安装包。"); }
        }

        public static void DiscardInstaller(string path)
        {
            if (!StagedPath(path)) return;
            string folder = Path.GetDirectoryName(path);
            try
            {
                if (!Directory.Exists(folder)) return;
                CheckDirectory(UpdateCache);
                CheckDirectory(folder);
                File.Delete(path);
                Directory.Delete(folder, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static void CleanStaleDownloads()
        {
            try
            {
                if (!Directory.Exists(UpdateCache)) return;
                CheckDirectory(UpdateCache);
                DateTime cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (string folder in Directory.EnumerateDirectories(UpdateCache))
                {
                    Guid id;
                    if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out id) ||
                        (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || Directory.GetLastWriteTimeUtc(folder) > cutoff) continue;
                    foreach (string file in Directory.EnumerateFiles(folder))
                    {
                        string candidate = file.EndsWith(".partial", StringComparison.Ordinal) ? file.Substring(0, file.Length - 8) : file;
                        if (StagedPath(candidate) && File.GetLastWriteTimeUtc(file) <= cutoff) TryDelete(file);
                    }
                    try { Directory.Delete(folder, false); } catch (IOException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
