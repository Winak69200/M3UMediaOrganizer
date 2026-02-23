using System.IO;

namespace M3UMediaOrganizer.Services;

public static class ExistingIndex
{
    public static string NormalizeFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    public static HashSet<string> Build(string root)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sub in new[] { "Films", "Series", "Autre" })
        {
            var baseDir = Path.Combine(root, sub);
            if (!Directory.Exists(baseDir)) continue;

            try
            {
                foreach (var p in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
                    set.Add(NormalizeFullPath(p));
            }
            catch
            {
                // ignore IO/permissions
            }
        }

        return set;
    }
}