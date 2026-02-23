using M3UMediaOrganizer.Models;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace M3UMediaOrganizer.Services;

public sealed class M3uParser
{
    static readonly Regex RxGroup = new(@"group-title=""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RxTvgName = new(@"tvg-name=""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RxLogo = new(@"tvg-logo=""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex RxSE1 = new(@"\bS(\d{1,2})\s*E(\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RxSE2 = new(@"\b(\d{1,2})x(\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly string[] ExcludedGroupPrefixes =
    [
        "AR:", "ES:", "EN:", "IT:", "ALBANIA", "BELGIUM", "Films VOST",
        "Séries ARABES", "Séries SUB-AR", "Séries TURQUES", "DZ:"
    ];

    static readonly HashSet<string> LiveGroups = new(StringComparer.OrdinalIgnoreCase)
    { "news","sport","sports","tv","live","documentary","kids","music" };

    public sealed record ParseProgress(int Percent, long ReadBytes, long TotalBytes, int ItemsCount);

    public async Task<List<M3uItem>> ParseByBytesAsync(
        string path,
        IProgress<ParseProgress>? progress,
        CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Fichier introuvable", path);

        var fi = new FileInfo(path);
        long totalBytes = Math.Max(1, fi.Length);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var (encoding, bomLen) = DetectEncoding(fs);
        fs.Position = bomLen;

        using var sr = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1024 * 1024, leaveOpen: true);

        var items = new List<M3uItem>(capacity: 4096);

        progress?.Report(new ParseProgress(0, fs.Position, totalBytes, 0));

        int lastReportTick = Environment.TickCount;
        string? lastExtInf = null;

        while (!sr.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var line = await sr.ReadLineAsync().ConfigureAwait(false);
            if (line is null) break;

            // Normalisations rapides
            if (line.Length > 0 && line[0] == '\uFEFF') line = line[1..];
            if (line.IndexOf('\0') >= 0) line = line.Replace("\0", "");

            if (line.Length == 0) continue;
            var l = line;
            if (l[0] <= ' ' || l[^1] <= ' ')
            {
                l = l.Trim();
                if (l.Length == 0) continue;
            }

            if (l.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                lastExtInf = l;
                continue;
            }

            if (lastExtInf is not null && !l.StartsWith("#", StringComparison.Ordinal))
            {
                var meta = ParseExtInf(lastExtInf);
                var title = meta.Title;
                var group = meta.GroupTitle;
                var url = l;

                // Exclusions group-title
                if (!string.IsNullOrWhiteSpace(group) &&
                    ExcludedGroupPrefixes.Any(p => group.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    lastExtInf = null;
                    continue;
                }

                var (season, episode, hasSE) = GetSeasonEpisode(title);
                var ext = PathPlanner.TryGetExtensionFromUrl(url);
                var mediaType = InferMediaType(title, group, url, hasSE);

                // Skip Live
                if (mediaType == "Live")
                {
                    lastExtInf = null;
                    continue;
                }

                items.Add(new M3uItem
                {
                    Selected = false,
                    MediaType = mediaType,
                    GroupTitle = group ?? "",
                    Title = title,
                    Season = season,
                    Episode = episode,
                    Ext = ext ?? "",
                    Exists = false,
                    TargetPath = "",
                    SourceUrl = url,
                    Status = ""
                });

                lastExtInf = null;
            }

            int now = Environment.TickCount;
            if (now - lastReportTick >= 200)
            {
                long pos = fs.Position;
                int percent = (int)Math.Min(100, Math.Round((pos / (double)totalBytes) * 100.0, 0));
                progress?.Report(new ParseProgress(percent, pos, totalBytes, items.Count));
                lastReportTick = now;
            }
        }

        progress?.Report(new ParseProgress(100, totalBytes, totalBytes, items.Count));
        return items;
    }

    private static (Encoding Encoding, int BomLen) DetectEncoding(FileStream fs)
    {
        var head = new byte[2048];
        int read = fs.Read(head, 0, head.Length);
        fs.Position = 0;

        // BOM
        if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            return (Encoding.UTF8, 3);

        if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            return (Encoding.Unicode, 2);

        if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            return (Encoding.BigEndianUnicode, 2);

        // Heuristique UTF-16 sans BOM
        int zerosEven = 0, zerosOdd = 0;
        int pairs = Math.Min(read, 2048);
        for (int i = 0; i < pairs; i++)
        {
            if (head[i] == 0)
            {
                if ((i % 2) == 0) zerosEven++;
                else zerosOdd++;
            }
        }

        double ratioEven = pairs > 0 ? zerosEven / (double)pairs : 0;
        double ratioOdd = pairs > 0 ? zerosOdd / (double)pairs : 0;

        if (ratioOdd > 0.20 && ratioOdd > ratioEven * 2) return (Encoding.Unicode, 0);
        if (ratioEven > 0.20 && ratioEven > ratioOdd * 2) return (Encoding.BigEndianUnicode, 0);

        return (Encoding.UTF8, 0);
    }

    private static (string GroupTitle, string Title, string DisplayTitle, string TvGName, string Logo) ParseExtInf(string extInf)
    {
        string group = "", tvgName = "", logo = "", displayTitle = "";

        var mg = RxGroup.Match(extInf); if (mg.Success) group = mg.Groups[1].Value;
        var mn = RxTvgName.Match(extInf); if (mn.Success) tvgName = mn.Groups[1].Value;
        var ml = RxLogo.Match(extInf); if (ml.Success) logo = ml.Groups[1].Value;

        int idx = extInf.LastIndexOf(',');
        if (idx >= 0 && idx < extInf.Length - 1)
            displayTitle = extInf[(idx + 1)..].Trim();

        string title = !string.IsNullOrWhiteSpace(tvgName) ? tvgName :
                       !string.IsNullOrWhiteSpace(displayTitle) ? displayTitle :
                       "Sans titre";

        return (group, title, displayTitle, tvgName, logo);
    }

    private static (int? Season, int? Episode, bool HasSE) GetSeasonEpisode(string title)
    {
        var m1 = RxSE1.Match(title);
        if (m1.Success)
            return (int.Parse(m1.Groups[1].Value), int.Parse(m1.Groups[2].Value), true);

        var m2 = RxSE2.Match(title);
        if (m2.Success)
            return (int.Parse(m2.Groups[1].Value), int.Parse(m2.Groups[2].Value), true);

        return (null, null, false);
    }

    private static string InferMediaType(string title, string groupTitle, string url, bool hasSE)
    {
        var t = ((title ?? "") + " " + (groupTitle ?? "") + " " + (url ?? "")).ToLowerInvariant();

        if (hasSE || t.Contains("/series/") || Regex.IsMatch(t, @"\b(saison|season|episodes|épisode|episode)\b", RegexOptions.IgnoreCase))
            return "Serie";

        if (t.Contains("/movie/") || Regex.IsMatch(t, @"\.(mkv|mp4|avi|mov|wmv|m4v)\b", RegexOptions.IgnoreCase))
            return "Film";

        if (!string.IsNullOrWhiteSpace(groupTitle) && LiveGroups.Contains(groupTitle.Trim()))
            return "Live";

        var ext = PathPlanner.TryGetExtensionFromUrl(url ?? "");
        if (string.IsNullOrWhiteSpace(ext))
            return "Live";

        return "Autre";
    }
}