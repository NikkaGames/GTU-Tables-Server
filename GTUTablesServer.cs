using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

public sealed class GTUTablesServer : IDisposable
{
    private const string CipherKey = "table";
    private const int MaxClients = 64;
    private const int MaxRequestBytes = 4096;
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxHtmlChars = 16 * 1024 * 1024;
    private const int MaxTimetableSources = 64;
    private const int MaxConcurrentDownloads = 4;
    private static readonly Uri HomeUri = new("https://leqtori.gtu.ge/");
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(2);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ClientIdleTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex AnchorRegex = new(
        @"<a\b(?<attributes>[^>]*)>(?<label>.*?)</a\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline,
        TimeSpan.FromSeconds(2));

    private static readonly Regex HrefRegex = new(
        """\bhref\s*=\s*(?:"(?<url>[^"]+)"|'(?<url>[^']+)'|(?<url>[^\s>]+))""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    private static readonly Regex HtmlTagsRegex = new(
        @"<[^>]+>", RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    private static readonly Regex GroupLabelRegex = new(
        @"groups|ჯგუფ", RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    private static readonly Regex TeacherLabelRegex = new(
        @"teachers|lecturers|პედაგოგ", RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    private static readonly Regex RoomLabelRegex = new(
        @"rooms|აუდიტორი", RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    private static readonly Regex TeacherFilenameRegex = new(
        @"prof_teachers\.html?", RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    private static readonly TimeZoneInfo GeorgiaTimeZone = ResolveGeorgiaZone();

    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _clientSlots = new(MaxClients, MaxClients);
    private readonly HttpClient _httpClient;
    private TimetableSnapshot? _snapshot;
    private bool _disposed;

    public GTUTablesServer(IPAddress address, int port)
    {
        _listener = new TcpListener(address, port);
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli
        })
        {
            Timeout = TimeSpan.FromSeconds(40)
        };
    }

    public async Task RunAsync(CancellationToken shutdownToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        CancellationToken token = lifetime.Token;
        var clientTasks = new List<Task>(MaxClients);

        _listener.Start();
        Console.WriteLine($"Listening on {_listener.LocalEndpoint}");

        Task refreshTask = RefreshLoopAsync(token);
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _clientSlots.WaitAsync(token);
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(token);
                }
                catch
                {
                    _clientSlots.Release();
                    throw;
                }

                clientTasks.RemoveAll(static task => task.IsCompleted);
                clientTasks.Add(ServeClientAsync(client, token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Cancel();
            _listener.Stop();
            await Task.WhenAll(clientTasks);
            await refreshTask;
        }
    }
    public void Start() => RunAsync().GetAwaiter().GetResult();

    private async Task RefreshLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TimeSpan delay = RefreshInterval;
            try
            {
                (string homepage, Uri finalUri) = await FetchHtmlAsync(HomeUri, token);
                IReadOnlyList<TimetableLink> links = DiscoverTimetables(homepage, finalUri);
                if (links.Count == 0)
                {
                    Console.Error.WriteLine(DescribeDiscoveryFailure(homepage, finalUri));
                    throw new InvalidDataException("No GTU HTML timetable links found.");
                }

                TimetableSnapshot? previous = Volatile.Read(ref _snapshot);
                using var downloadSlots = new SemaphoreSlim(MaxConcurrentDownloads);
                Task<TimetableFetch>[] downloads = links.Select(async link =>
                {
                    await downloadSlots.WaitAsync(token);
                    try
                    {
                        (string html, _) = await FetchHtmlAsync(link.Uri, token);
                        if (!TimetableRenderer.LooksLikeTimetable(html))
                            throw new InvalidDataException("Not a supported GTU HTML timetable.");
                        return new TimetableFetch(link, html, null);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        return new TimetableFetch(link, null, ex);
                    }
                    finally
                    {
                        downloadSlots.Release();
                    }
                }).ToArray();

                TimetableFetch[] results = await Task.WhenAll(downloads);
                var pages = new List<TimetablePage>(results.Length);
                int updated = 0;
                int failed = 0;

                foreach (TimetableFetch result in results)
                {
                    if (result.Html is not null)
                    {
                        pages.Add(new TimetablePage(result.Link, result.Html));
                        updated++;
                        continue;
                    }

                    failed++;
                    Console.Error.WriteLine($"Timetable unavailable: {result.Link.Uri.AbsoluteUri} ({result.Error?.Message})");
                    TimetablePage? old = previous?.Pages.FirstOrDefault(page => page.Link.Uri == result.Link.Uri);
                    if (old is not null)
                        pages.Add(new TimetablePage(result.Link, old.Html, stale: true));
                }

                if (pages.Count == 0)
                    throw new InvalidDataException($"All {links.Count} timetable sources failed; retaining prior snapshot.");
                pages.Sort(static (a, b) =>
                {
                    int priority = a.Link.Category.CompareTo(b.Link.Category);
                    return priority != 0 ? priority : a.Link.Order.CompareTo(b.Link.Order);
                });
                Volatile.Write(ref _snapshot, new TimetableSnapshot(pages.ToArray()));
                Console.WriteLine($"Timetables refreshed: {updated} downloaded, {pages.Count - updated} cached, " +
                                  $"{failed} failed, {pages.Count} available at {DateTimeOffset.UtcNow:O}");
                if (failed > 0)
                    delay = RetryInterval;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Refresh failed: {ex}");
                delay = RetryInterval;
            }

            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<(string Html, Uri FinalUri)> FetchHtmlAsync(Uri uri, CancellationToken token)
    {
        Console.WriteLine($"Fetching {uri.AbsoluteUri}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        CancellationToken requestToken = timeout.Token;
        using HttpResponseMessage response = await _httpClient.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, requestToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxHtmlChars)
            throw new InvalidDataException($"HTML exceeds {MaxHtmlChars} bytes.");

        Encoding encoding = Encoding.UTF8;
        string? charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch (ArgumentException) { }
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(requestToken);
        using var reader = new StreamReader(body, encoding, detectEncodingFromByteOrderMarks: true);
        var html = new StringBuilder();
        char[] chars = new char[16 * 1024];

        int read;
        while ((read = await reader.ReadAsync(chars.AsMemory(), requestToken)) != 0)
        {
            if (html.Length + read > MaxHtmlChars)
                throw new InvalidDataException($"HTML exceeds {MaxHtmlChars} characters.");
            html.Append(chars, 0, read);
        }

        return (html.ToString(), response.RequestMessage?.RequestUri ?? uri);
    }
    internal static IReadOnlyList<TimetableLink> DiscoverTimetables(string homepage, Uri baseUri)
    {
        var found = new Dictionary<string, TimetableLink>(StringComparer.OrdinalIgnoreCase);
        int order = 0;

        void Add(string raw, string label, TimetableCategory? categoryOverride = null)
        {
            if (!TryMakeGtURi(baseUri, raw, out Uri? uri))
                return;
            TimetableCategory? category = categoryOverride ?? ClassifyTimetable(uri!, label);
            if (category is null)
                return;

            string key = uri!.AbsoluteUri;
            if (!found.TryGetValue(key, out TimetableLink? old))
            {
                found.Add(key, new TimetableLink(uri, category.Value, label, order++));
            }
            else if (category.Value < old.Category)
            {
                found[key] = old with { Category = category.Value, Label = label };
            }
        }

        foreach (Match anchor in AnchorRegex.Matches(homepage))
        {
            Match href = HrefRegex.Match(anchor.Groups["attributes"].Value);
            if (!href.Success)
                continue;
            string label = WebUtility.HtmlDecode(HtmlTagsRegex.Replace(
                anchor.Groups["label"].Value, string.Empty)).Trim();
            Add(href.Groups["url"].Value, label);
        }
        MatchCollection allLinks = HrefRegex.Matches(homepage);
        for (int i = 0; i < allLinks.Count; i++)
        {
            string raw = allLinks[i].Groups["url"].Value;
            Add(raw, string.Empty);
            if (TryMakeGtURi(baseUri, raw, out Uri? current) &&
                (TeacherFilenameRegex.IsMatch(current!.AbsolutePath) ||
                 ClassifyTimetable(current, string.Empty) == TimetableCategory.Teachers) &&
                i + 1 < allLinks.Count &&
                allLinks[i + 1].Index - allLinks[i].Index < 2500)
            {
                string next = allLinks[i + 1].Groups["url"].Value;
                if (TryMakeGtURi(baseUri, next, out Uri? nextUri) &&
                    ClassifyTimetable(nextUri!, string.Empty) is null)
                    Add(next, "Groups (legacy link ordering)", TimetableCategory.Groups);
            }
        }
        string? legacyUrl = Environment.GetEnvironmentVariable("GTU_GROUPS_URL");
        if (!string.IsNullOrWhiteSpace(legacyUrl))
            Add(legacyUrl, "Configured Groups", TimetableCategory.Groups);

        string? manualUrls = Environment.GetEnvironmentVariable("GTU_TIMETABLE_URLS");
        if (!string.IsNullOrWhiteSpace(manualUrls))
        {
            foreach (string entry in manualUrls.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Add(entry, "Configured timetable", TimetableCategory.Additional);
        }

        if (found.Count > MaxTimetableSources)
            throw new InvalidDataException($"GTU advertised {found.Count} HTML timetables; limit is {MaxTimetableSources}.");

        return found.Values.OrderBy(link => link.Order).ToArray();
    }
    internal static IReadOnlyList<Uri> FindGroupsUris(string homepage, Uri baseUri) =>
        DiscoverTimetables(homepage, baseUri)
            .Where(link => link.Category == TimetableCategory.Groups)
            .Select(link => link.Uri)
            .ToArray();

    private static TimetableCategory? ClassifyTimetable(Uri uri, string label)
    {
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            return null;

        string filename = Path.GetFileName(path);
        if (filename.Contains("group", StringComparison.OrdinalIgnoreCase) ||
            GroupLabelRegex.IsMatch(label))
            return TimetableCategory.Groups;
        if (filename.Contains("teacher", StringComparison.OrdinalIgnoreCase) ||
            TeacherLabelRegex.IsMatch(label))
            return TimetableCategory.Teachers;
        if (filename.Contains("room", StringComparison.OrdinalIgnoreCase) ||
            RoomLabelRegex.IsMatch(label))
            return TimetableCategory.Rooms;
        if (path.StartsWith("/public/", StringComparison.OrdinalIgnoreCase))
            return TimetableCategory.Additional;

        return null;
    }

    private static bool TryMakeGtURi(Uri baseUri, string raw, out Uri? uri)
    {
        uri = null;
        raw = WebUtility.HtmlDecode(raw).Trim();
        if (raw.Length == 0 || !Uri.TryCreate(baseUri, raw, out Uri? parsed))
            return false;
        if (!parsed.Host.Equals(HomeUri.Host, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (parsed.GetLeftPart(UriPartial.Path) == baseUri.GetLeftPart(UriPartial.Path) &&
            parsed.Query == baseUri.Query)
            return false;

        uri = parsed;
        return true;
    }

    private static string DescribeDiscoveryFailure(string html, Uri uri)
    {
        Match group = GroupLabelRegex.Match(html);
        string nearGroup = group.Success
            ? html.Substring(Math.Max(0, group.Index - 240),
                Math.Min(html.Length - Math.Max(0, group.Index - 240), 650))
            : "<no Groups/ჯგუფ label>";
        nearGroup = Regex.Replace(nearGroup, @"\s+", " ");
        MatchCollection links = HrefRegex.Matches(html);
        string examples = string.Join(", ", links.Cast<Match>()
            .Take(12).Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value)));
        return $"Timetable link discovery failed. Final URI: {uri}; " +
               $"HTML length: {html.Length}; href count: {links.Count}; " +
               $"href samples: [{examples}]; around Groups label: {nearGroup}";
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken shutdownToken)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using NetworkStream stream = client.GetStream();
                Console.WriteLine($"Connected: {client.Client.RemoteEndPoint}");

                while (!shutdownToken.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                    timeout.CancelAfter(ClientIdleTimeout);

                    string? groupId = await ReadRequestAsync(stream, timeout.Token);
                    if (groupId is null)
                        break;

                    byte[] content = Encoding.UTF8.GetBytes(Encrypt(GetData(groupId), CipherKey));
                    if (content.Length > MaxResponseBytes)
                        content = Encoding.UTF8.GetBytes(Encrypt("RESPONSE_TOO_LARGE", CipherKey));

                    byte[] prefix = Encoding.ASCII.GetBytes($"{content.Length}L");
                    await stream.WriteAsync(prefix, timeout.Token);
                    await stream.WriteAsync(content, timeout.Token);
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
        {
            Console.Error.WriteLine($"Client connection closed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unhandled client error: {ex}");
        }
        finally
        {
            _clientSlots.Release();
        }
    }

    private static async Task<string?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] single = new byte[1];
        int length = 0;
        int digits = 0;

        while (true)
        {
            int read = await stream.ReadAsync(single, token);
            if (read == 0)
            {
                if (digits != 0)
                    throw new EndOfStreamException("Incomplete request header.");
                return null;
            }

            byte ch = single[0];
            if (ch == (byte)'L')
            {
                if (digits == 0 || length == 0)
                    throw new InvalidDataException("Empty request length.");
                break;
            }

            if (ch < (byte)'0' || ch > (byte)'9' || digits >= 9)
                throw new InvalidDataException("Malformed request length prefix.");

            length = checked(length * 10 + ch - (byte)'0');
            if (length > MaxRequestBytes)
                throw new InvalidDataException("Request exceeds size limit.");
            digits++;
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        string decrypted = Decrypt(Encoding.UTF8.GetString(payload), CipherKey).Trim();
        if (decrypted.Length is < 1 or > 128 ||
            decrypted.IndexOfAny(['<', '>', '\r', '\n', '\0']) >= 0)
        {
            throw new InvalidDataException("Invalid group ID.");
        }

        return decrypted;
    }

    internal string GetData(string request)
    {
        TimetableSnapshot? snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null)
            return "SERVICE_UNAVAILABLE";

        TimetableCategory? filter = null;
        string id = request;
        int separator = request.IndexOf(':');
        if (separator is > 0 and < 12)
        {
            string prefix = request[..separator].ToLowerInvariant();
            filter = prefix switch
            {
                "group" or "groups" => TimetableCategory.Groups,
                "teacher" or "teachers" => TimetableCategory.Teachers,
                "room" or "rooms" => TimetableCategory.Rooms,
                "additional" or "faculty" => TimetableCategory.Additional,
                _ => null
            };
            if (filter is not null)
                id = request[(separator + 1)..].Trim();
        }

        if (id.Length == 0)
            return "NOT_FOUND";

        string key = filter is null ? $"any:{id}" : $"{(int)filter.Value}:{id}";
        string? content = snapshot.Cache.GetOrCreate(key,
            () => FindMatchingTables(snapshot.Pages, filter, id));
        if (content is null)
            return "NOT_FOUND";

        DateTimeOffset georgianNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, GeorgiaTimeZone);
        return TimetableRenderer.Render(content, georgianNow);
    }

    private static string? FindMatchingTables(TimetablePage[] pages, TimetableCategory? filter, string id)
    {
        var matches = new List<(string Label, string Table)>();
        foreach (TimetablePage page in pages)
        {
            if (filter is not null && page.Link.Category != filter.Value)
                continue;
            if (filter is null && matches.Count > 0 &&
                page.Link.Category >= TimetableCategory.Teachers)
                break;

            string? section = TimetableRenderer.ExtractGroupTable(page.Html, id);
            if (section is null)
                continue;

            string label = string.IsNullOrWhiteSpace(page.Link.Label)
                ? Path.GetFileName(Uri.UnescapeDataString(page.Link.Uri.AbsolutePath))
                : page.Link.Label;
            matches.Add((label, section));
        }

        if (matches.Count == 0)
            return null;

        return matches.Count == 1
            ? matches[0].Table
            : string.Join("\n", matches.Select(match =>
                $"<h3>{WebUtility.HtmlEncode(match.Label)}</h3>\n{match.Table}"));
    }

    public static string Encrypt(string input, string key) => Transform(input, key, +1);
    public static string Decrypt(string input, string key) => Transform(input, key, -1);

    private static string Transform(string input, string key, int direction)
    {
        if (string.IsNullOrEmpty(key))
            return input;

        char[] result = new char[input.Length];
        for (int i = 0; i < input.Length; i++)
            result[i] = unchecked((char)(input[i] + direction * key[i % key.Length]));
        return new string(result);
    }

    private static TimeZoneInfo ResolveGeorgiaZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Tbilisi");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Georgian Standard Time");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _listener.Stop();
        _httpClient.Dispose();
        _clientSlots.Dispose();
    }

    internal enum TimetableCategory
    {
        Groups = 0,
        Additional = 1,
        Teachers = 2,
        Rooms = 3
    }

    internal sealed record TimetableLink(Uri Uri, TimetableCategory Category, string Label, int Order);
    private sealed record TimetableFetch(TimetableLink Link, string? Html, Exception? Error);
    internal void SetTimetablesForTests(params (TimetableLink Link, string Html)[] sources) =>
        Volatile.Write(ref _snapshot, new TimetableSnapshot(
            sources.Select(source => new TimetablePage(source.Link, source.Html))
                   .OrderBy(page => page.Link.Category)
                   .ThenBy(page => page.Link.Order)
                   .ToArray()));

    internal int CachedTableCountForTests => Volatile.Read(ref _snapshot)?.Cache.Count ?? 0;

    private sealed class TimetableSnapshot(TimetablePage[] pages)
    {
        public TimetablePage[] Pages { get; } = pages;
        public TimetableCache Cache { get; } = new();
    }

    private sealed class TimetablePage(TimetableLink link, string html, bool stale = false)
    {
        public TimetableLink Link { get; } = link;
        public string Html { get; } = html;
        public bool Stale { get; } = stale;
    }
}
