using System.Net;

internal static class SelfTests
{
    public static void Run()
    {
        const string id = "108434-1";
        string encoded = GTUTablesServer.Encrypt(id, "table");
        Assert(GTUTablesServer.Decrypt(encoded, "table") == id, "Cipher compatibility");

        static string Table(string name) =>
            $"<html><table><tr><th>{name}</th></tr>" +
            "<tr><th class=\"xAxis\">ხუთშ./Thurs.</th></tr>" +
            "<tr><th class=\"yAxis\">1-9:00</th><td>Math</td></tr>" +
            "</table></html>";

        string sample = Table(id) + Table("other");
        string? extracted = TimetableRenderer.ExtractGroupTable(sample, id);
        Assert(extracted is not null && !extracted.Contains("other"), "Table extraction");
        Assert(TimetableRenderer.ExtractGroupTable(sample, "missing") is null, "Missing ID");
        Assert(TimetableRenderer.LooksLikeTimetable(sample), "Timetable HTML check");
        Assert(!TimetableRenderer.LooksLikeTimetable("<html>error</html>"), "Reject error HTML");

        string highlighted = TimetableRenderer.Render(extracted!,
            new DateTimeOffset(2026, 10, 29, 9, 30, 0, TimeSpan.FromHours(4)));
        Assert(highlighted.Contains("(NOW)") && highlighted.Contains("(TODAY)"), "Time/day highlights");
        Assert(!highlighted.Contains("name=\"viewport\"", StringComparison.OrdinalIgnoreCase),
            "WebView overview scaling is not overridden by an HTML viewport");
        Assert(!highlighted.Contains("zoom:", StringComparison.OrdinalIgnoreCase),
            "Footer no longer uses CSS zoom");
        Assert(highlighted.Contains(".center-div{display:flex;flex-direction:column;") &&
               highlighted.Contains(".inf{display:flex;align-items:center;justify-content:center;") &&
               highlighted.Contains("width:fit-content;min-width:calc(100% - 16px)"),
            "Creator links are centered below the heading across the timetable width");
        string ending = TimetableRenderer.Render(extracted!,
            new DateTimeOffset(2026, 10, 29, 9, 55, 0, TimeSpan.FromHours(4)));
        Assert(ending.Contains("(ENDING SOON)"), "End-of-hour highlight");

        var homepage = """
            <h1>VI კვირის ცხრილი</h1>
            <p><a href="http://leqtori.gtu.ge:9000/public/teachers 2026_2027_I_6.html">პედაგოგები/Teachers</a></p>
            <p><a href="http://leqtori.gtu.ge:9000/public/groups 2026_2027_I_6.html">ჯგუფები/Groups</a></p>
            <p><a href="http://leqtori.gtu.ge:9000/public/rooms 2026_2027_I_6.html">აუდიტორიები/Rooms</a></p>
            <h2>დამატებითი სასწავლო კურსების ცხრილი</h2>
            <p><a href="http://leqtori.gtu.ge:9000/public/construction 2026_2027_I.html">Faculty of Construction</a></p>
            <p><a href="http://leqtori.gtu.ge:9000/public/energy 2026_2027_I.html">Faculty of Power Engineering</a></p>
            <p><a href="/2026__2027/I/B/archive">არქივი</a></p>
            <p><a href="https://gtu.ge">GTU</a></p>
            """;
        Uri baseUri = new("https://leqtori.gtu.ge/2026__2027/I/B/info");
        IReadOnlyList<GTUTablesServer.TimetableLink> links =
            GTUTablesServer.DiscoverTimetables(homepage, baseUri);
        Assert(links.Count == 5, "Discover all current timetable links without navigation links");
        Assert(links.Count(link => link.Category == GTUTablesServer.TimetableCategory.Groups) == 1,
            "Classify group tables");
        Assert(links.Count(link => link.Category == GTUTablesServer.TimetableCategory.Teachers) == 1,
            "Classify teacher tables");
        Assert(links.Count(link => link.Category == GTUTablesServer.TimetableCategory.Rooms) == 1,
            "Classify room tables");
        Assert(links.Count(link => link.Category == GTUTablesServer.TimetableCategory.Additional) == 2,
            "Discover all faculty tables");
        Assert(links.All(link => link.Uri.Port == 9000 && link.Uri.Scheme == "http"),
            "Preserve host, port and HTTP scheme");
        Assert(links.Any(link => link.Uri.AbsoluteUri.Contains("groups%202026_2027_I_6.html")),
            "Percent-encode spaces in filenames");
        Assert(GTUTablesServer.FindGroupsUris(homepage, baseUri).Count == 1,
            "Legacy Groups discovery helper");

        const string oldHomepage = """
            <a href="/2026__2027/I/B/prof_teachers.html">Teachers</a>
            <a href="/2026__2027/I/B/timetable_x3.html">rs</a>
            <div>ჯგუფები / Groups</div>
            """;
        Assert(GTUTablesServer.FindGroupsUris(oldHomepage, baseUri).Count == 1,
            "Legacy adjacent Groups discovery");

        using (var server = new GTUTablesServer(IPAddress.Loopback, 0))
        {
            GTUTablesServer.TimetableLink Groups() => links.Single(link =>
                link.Category == GTUTablesServer.TimetableCategory.Groups);
            GTUTablesServer.TimetableLink Teacher() => links.Single(link =>
                link.Category == GTUTablesServer.TimetableCategory.Teachers);
            GTUTablesServer.TimetableLink Room() => links.Single(link =>
                link.Category == GTUTablesServer.TimetableCategory.Rooms);
            GTUTablesServer.TimetableLink[] faculty = links.Where(link =>
                link.Category == GTUTablesServer.TimetableCategory.Additional).ToArray();

            server.SetTimetablesForTests(
                (Groups(), Table("100-1")),
                (Teacher(), Table("Jane Doe")),
                (Room(), Table("06-411")),
                (faculty[0], Table("200-1") + Table("100-1")),
                (faculty[1], Table("300-1")));

            string combined = server.GetData("100-1");
            Assert(combined.Contains("100-1"), "Existing plain group ID");
            Assert(combined.Split("100-1", StringSplitOptions.None).Length == 3,
                "Group listed in both main and faculty schedules is combined");
            Assert(server.GetData("200-1").Contains("200-1"), "First faculty timetable");
            Assert(server.GetData("300-1").Contains("300-1"), "Second faculty timetable");
            Assert(server.GetData("teachers:Jane Doe").Contains("Jane Doe"), "Teacher selection");
            Assert(server.GetData("rooms:06-411").Contains("06-411"), "Room selection");
            Assert(server.GetData("groups:200-1") == "NOT_FOUND", "Category filter");
            Assert(server.GetData("unknown-id") == "NOT_FOUND", "No false matches");
            Assert(server.CachedTableCountForTests == 5, "Only successful lookups are cached");
            server.SetTimetablesForTests((Groups(), Table("replacement-1")));
            Assert(server.CachedTableCountForTests == 0, "Published refresh invalidates cached tables");
            Assert(server.GetData("100-1") == "NOT_FOUND", "Old cached result not returned after refresh");
            Assert(server.GetData("replacement-1").Contains("replacement-1"), "New snapshot is available");
            Assert(server.CachedTableCountForTests == 1, "New snapshot uses an independent cache");
        }
        var cache = new TimetableCache();
        int extractions = 0;
        Assert(cache.GetOrCreate("missing", () => null) is null && cache.Count == 0,
            "Missing tables are never cached");
        for (int i = 0; i < 30; i++)
        {
            string key = $"group-{i}";
            string? value = cache.GetOrCreate(key, () =>
            {
                extractions++;
                return $"table-{i}";
            });
            Assert(value == $"table-{i}", "Cache insertion");
        }
        Assert(cache.Count == 30 && extractions == 30, "Exactly 30 cache entries");
        Assert(cache.GetOrCreate("GROUP-0", () => throw new Exception("Cache hit rescanned")) == "table-0",
            "Cache hit avoids extraction and respects case-insensitive IDs");
        Assert(cache.GetOrCreate("group-30", () => "table-30") == "table-30",
            "31st unique table remains available");
        Assert(cache.Count == 30 && cache.Contains("group-0") && !cache.Contains("group-1"),
            "Least recently used entry is evicted at capacity");
        Assert(cache.GetOrCreate("group-1", () => "new-table-1") == "new-table-1",
            "Evicted table is extracted again");
        Assert(cache.Count == 30, "Capacity remains bounded");

        TimeZoneInfo tbilisi = ResolveZone("Asia/Tbilisi", "Georgian Standard Time");
        TimeZoneInfo berlin = ResolveZone("Europe/Berlin", "W. Europe Standard Time");
        CheckOffset(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), 3);
        CheckOffset(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero), 2);

        Console.WriteLine("All self-tests passed.");

        void CheckOffset(DateTimeOffset utc, int expectedHours)
        {
            TimeSpan difference = TimeZoneInfo.ConvertTime(utc, tbilisi).Offset -
                                  TimeZoneInfo.ConvertTime(utc, berlin).Offset;
            Assert(difference == TimeSpan.FromHours(expectedHours), "Germany/Georgia DST offset");
        }
    }

    private static TimeZoneInfo ResolveZone(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    private static void Assert(bool condition, string reason)
    {
        if (!condition)
            throw new Exception($"Self-test failed: {reason}");
    }
}
