using System.Net;
using System.Text.RegularExpressions;

internal static class TimetableRenderer
{
    private static readonly string[] DayNames =
    [
        "კვი./Sun.",
        "ორშ./Mon.",
        "სამშ./Tues.",
        "ოთხშ./Wed.",
        "ხუთშ./Thurs.",
        "პარ./Fri.",
        "შაბ./Sat."
    ];

    private static readonly Regex TableTags = new(
        @"</?table\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    private static readonly Regex EmptyCell = new(
        @"<td\s*>\s*---\s*</td\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    public static bool LooksLikeTimetable(string html) =>
        html.Contains("<table", StringComparison.OrdinalIgnoreCase) &&
        html.Contains("</table", StringComparison.OrdinalIgnoreCase) &&
        html.Contains("<th", StringComparison.OrdinalIgnoreCase) &&
        html.Contains("</tr", StringComparison.OrdinalIgnoreCase);

    public static string? ExtractGroupTable(string html, string groupId)
    {
        string headerPattern = @"(?<=>)\s*" + Regex.Escape(groupId) + @"\s*</th\s*>\s*</tr\s*>";
        Match header = Regex.Match(html, headerPattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        if (!header.Success)
            return null;

        int tableStart = html.LastIndexOf("<table", header.Index, StringComparison.OrdinalIgnoreCase);
        if (tableStart < 0)
            return null;

        int depth = 0;
        foreach (Match tag in TableTags.Matches(html, tableStart))
        {
            depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            if (depth == 0)
                return html.Substring(tableStart, tag.Index + tag.Length - tableStart);
        }

        return null;
    }

    public static string Render(string table, DateTimeOffset now)
    {
        string day = DayNames[(int)now.DayOfWeek];
        if (now.DayOfWeek != DayOfWeek.Sunday)
        {
            table = HighlightTime(table, now.Hour,
                now.Minute >= 45 ? "ENDING SOON" : "NOW",
                now.Minute >= 45 ? "orange" : "red");

            table = HighlightTime(table, now.Hour + 1,
                now.Minute >= 55 ? "STARTING SOON" : "NEXT",
                now.Minute >= 55 ? "#009DD7" : "green");

            const string todayColor = "red";
            table = table.Replace(
                $"<th class=\"xAxis\">{day}</th>",
                $"<th class=\"xAxis\"><span style=\"color:{todayColor}\">{day} (TODAY)</span></th>",
                StringComparison.Ordinal);
        }

        return HtmlHead + table + HtmlFooter;
    }

    private static string HighlightTime(string html, int hour, string label, string color)
    {
        if (hour is < 9 or > 22)
            return html;

        string slot = $"{hour - 8}-{hour}:00";
        return html.Replace(
            $"<th class=\"yAxis\">{slot}</th>",
            $"<th class=\"yAxis\"><span style=\"color:{color}\">{slot}<br>({label})</span></th>",
            StringComparison.Ordinal);
    }
    public static string Inject(string html, string timeSlot, int emptyCellIndex,
        string subject, string teacher, string room)
    {
        int slot = html.IndexOf(timeSlot, StringComparison.Ordinal);
        if (slot < 0 || emptyCellIndex < 1)
            return html;

        int start = html.LastIndexOf("<tr", slot, StringComparison.OrdinalIgnoreCase);
        int end = html.IndexOf("</tr>", slot, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < 0)
            return html;

        end += "</tr>".Length;
        string row = html[start..end];
        int currentCell = 0;
        string replacement = "<td>" + Escape(subject) + "<br>" +
                             Escape(teacher) + "<br>" + Escape(room) + "<br></td>";

        string updated = EmptyCell.Replace(row, match =>
            ++currentCell == emptyCellIndex ? replacement : match.Value);
        return html[..start] + updated + html[end..];
    }

    private static string Escape(string text) =>
        WebUtility.HtmlEncode(text).Replace("\r\n", "\n", StringComparison.Ordinal)
                                   .Replace('\n', ' ');

    private const string HtmlHead = """
        <html xmlns="http://www.w3.org/1999/xhtml" lang="ge" xml:lang="ge">
        <head>
        <meta charset="utf-8">
        <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/4.7.0/css/font-awesome.min.css">
        <style>
        @import url(https://fonts.googleapis.com/css?family=Open+Sans);
        .inf{display:flex;align-items:center;justify-content:center;flex-wrap:wrap;gap:12px}
        .fa{display:inline-flex;align-items:center;justify-content:center;flex:0 0 52px;width:52px;height:52px;padding:0;font-size:26px;text-decoration:none;border-radius:50%;color:#fff}
        .fa-facebook{background:#3B5998}
        .fa-telegram{background:#25AFE2}
        .fa-instagram{background:#FF3ED8}
        .fa-github{background:#666}
        .center-div{display:flex;flex-direction:column;align-items:center;justify-content:center;box-sizing:border-box;width:100%;margin:32px auto 20px;gap:16px;text-align:center;font-size:24px;font-family:'Open Sans',sans-serif;color:#fff}
        table{border-collapse:collapse;font-family:'Open Sans';background:#1E1E1E;color:#fff}
        table td{padding:8px;color:#fff;border:1px solid #444}
        table thead td{background:#333;color:#fff;font-weight:bold;font-size:14px;border:1px solid #444}
        table tbody td{color:#fff;border:1px solid #555}
        table tbody tr{background:#1E1E1E}
        table tbody tr:nth-child(odd){background:#2A2A2A}
        .lecture{background:#1c7cd0;color:#fff}
        .lab{background:#52aa35;color:#fff}
        .practical{background:#de8f18;color:#fff}
        .practic{background:#c65316;color:#fff}
        .seminar{background:#761e8f;color:#fff}
        .course{background:#bc2a2a;color:#fff}
        body{background:#121212;color:#fff;width:fit-content;min-width:calc(100% - 16px)}
        </style>
        </head>
        <body>
        """;

    private const string HtmlFooter = """
        <br><br>
        <div class="center-div">
        <b>App Creator:</b>
        <div class="inf">
        <a href="https://t.me/s/NikkaGamesOfficial" class="fa fa-telegram"></a>
        <a href="https://facebook.com/sunflower.thrust" class="fa fa-facebook"></a>
        <a href="https://www.instagram.com/pmicdxe" class="fa fa-instagram"></a>
        <a href="https://github.com/NikkaGames" class="fa fa-github"></a>
        </div>
        </div>
        </body></html>
        """;
}
