using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TapRelay
{
    internal sealed class UpdateReleaseInfo
    {
        internal UpdateReleaseInfo(
            string version,
            string displayVersion,
            string name,
            string body,
            string releaseUrl,
            string downloadUrl)
        {
            Version = version;
            DisplayVersion = displayVersion;
            Name = name;
            Body = body;
            ReleaseUrl = releaseUrl;
            DownloadUrl = downloadUrl;
        }

        internal string Version { get; }
        internal string DisplayVersion { get; }
        internal string Name { get; }
        internal string Body { get; }
        internal string ReleaseUrl { get; }
        internal string DownloadUrl { get; }
        internal string TargetUrl => string.IsNullOrWhiteSpace(DownloadUrl) ? ReleaseUrl : DownloadUrl;
    }

    internal static class UpdateChecker
    {
        private const string LatestReleaseUrl =
            "https://api.github.com/repos/guliacer/TapRelay/releases/latest";
        private static readonly HttpClient HttpClient = CreateHttpClient();

        internal static string CurrentVersion
        {
            get
            {
                var version = typeof(UpdateChecker).Assembly.GetName().Version;
                return version == null ? "0.0.0" : NormalizeVersion(version);
            }
        }

        internal static async Task<UpdateReleaseInfo?> TryGetLatestAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                using var response = await HttpClient.GetAsync(
                    LatestReleaseUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var root = document.RootElement;
                var rawTag = GetString(root, "tag_name");
                if (!TryParseVersion(rawTag, out var latestVersion))
                {
                    return null;
                }

                if (!TryParseVersion(CurrentVersion, out var currentVersion) || latestVersion <= currentVersion)
                {
                    return null;
                }

                var releaseUrl = GetHttpUrl(root, "html_url");
                if (string.IsNullOrWhiteSpace(releaseUrl))
                {
                    return null;
                }

                var downloadUrl = FindWindowsAsset(root);
                var displayVersion = "v" + NormalizeVersion(latestVersion);
                return new UpdateReleaseInfo(
                    NormalizeVersion(latestVersion),
                    displayVersion,
                    GetString(root, "name"),
                    GetString(root, "body"),
                    releaseUrl,
                    downloadUrl);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception)
            {
                // 更新检查是附加能力，网络、JSON 或 GitHub 限流不能影响主程序启动。
                return null;
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(6)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TapRelay-PC/" + CurrentVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static string FindWindowsAsset(JsonElement root)
        {
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var url = GetHttpUrl(asset, "browser_download_url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }

            return string.Empty;
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }

        private static string GetHttpUrl(JsonElement element, string propertyName)
        {
            var value = GetString(element, propertyName);
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.AbsoluteUri
                : string.Empty;
        }

        private static bool TryParseVersion(string raw, out Version version)
        {
            version = new Version(0, 0, 0);
            var candidate = (raw ?? string.Empty).Trim().TrimStart('v', 'V');
            var separator = candidate.IndexOfAny(new[] { '-', '+' });
            if (separator >= 0)
            {
                candidate = candidate.Substring(0, separator);
            }

            if (!Version.TryParse(candidate, out var parsed) || parsed < new Version(0, 0, 0))
            {
                return false;
            }

            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }

        private static string NormalizeVersion(Version version)
        {
            return string.Format("{0}.{1}.{2}", version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }
}
