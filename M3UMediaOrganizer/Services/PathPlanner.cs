using System.Text;
using System.Text.RegularExpressions;
using M3UMediaOrganizer.Models;
using System.IO;
using System.Linq;

namespace M3UMediaOrganizer.Services;

public static class PathPlanner
{
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(invalid.Contains(ch) ? '_' : ch);

        var s = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(s) ? "SansTitre" : s;
    }

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidPathChars().Concat(Path.GetInvalidFileNameChars()).ToArray();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(invalid.Contains(ch) ? '_' : ch);

        var s = Regex.Replace(sb.ToString().Trim(), @"\s+", " ");
        return string.IsNullOrWhiteSpace(s) ? "Divers" : s;
    }

    public static string TryGetExtensionFromUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            return Path.GetExtension(u.AbsolutePath) ?? "";
        }
        catch
        {
            return Path.GetExtension(url) ?? "";
        }
    }

    public static string GetTargetPathFromItem(M3uItem item, string root)
    {
        var typeFolder = item.MediaType switch
        {
            "Film" => "Films",
            "Serie" => "Series",
            _ => "Autre"
        };

        var groupFolder = string.IsNullOrWhiteSpace(item.GroupTitle) ? "Divers" : SanitizeFolderName(item.GroupTitle);
        var ext = TryGetExtensionFromUrl(item.SourceUrl);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";

        // Séries: \Series\<Group>\<Titre>\Sxx\Exx.ext
        if (item.MediaType == "Serie")
        {
            var seriesFolder = SanitizeFolderName(item.Title);
            var targetDir = Path.Combine(root, typeFolder, groupFolder, seriesFolder);

            if (item.Season.HasValue && item.Episode.HasValue)
            {
                var seasonFolder = $"S{item.Season.Value:00}";
                targetDir = Path.Combine(targetDir, seasonFolder);
                var fileName = $"E{item.Episode.Value:00}{ext}";
                return Path.Combine(targetDir, fileName);
            }

            var safeTitle = SanitizeFileName(item.Title);
            return Path.Combine(targetDir, safeTitle + ext);
        }

        // Films/autre: plat
        {
            var safeTitle = SanitizeFileName(item.Title);
            var targetDir = Path.Combine(root, typeFolder, groupFolder);
            return Path.Combine(targetDir, safeTitle + ext);
        }
    }
}