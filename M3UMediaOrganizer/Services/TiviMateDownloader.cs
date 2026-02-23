using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace M3UMediaOrganizer.Services;

public sealed class TiviMateDownloader
{
    private readonly HttpClient _http;

    public sealed record DownloadProgress(
        long DownloadedBytes,
        long TotalBytes,
        double SpeedBytes,
        double Percent,
        string Status);

    public TiviMateDownloader()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromDays(1)
        };
    }

    public async Task DownloadOneAsync(
        string url,
        string path,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        long downloaded = 0;
        if (File.Exists(path))
        {
            var len = new FileInfo(path).Length;
            if (len > 0) downloaded = len;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        // HEADERS "TiviMate"
        req.Headers.UserAgent.ParseAdd("ExoPlayer/2.19.1");
        req.Headers.Accept.ParseAdd("application/vnd.apple.mpegurl, */*");
        req.Headers.Referrer = new Uri("http://aptip.top/");
        req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        req.Headers.TryAddWithoutValidation("Origin", "http://aptip.top");

        // Reprise
        if (downloaded > 0)
            req.Headers.Range = new RangeHeaderValue(downloaded, null);

        var sw = Stopwatch.StartNew();

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long totalSize = 0;
        if (resp.Content.Headers.ContentLength.HasValue)
        {
            // si on a demandé Range, ContentLength = taille restante; sinon taille totale
            totalSize = resp.Content.Headers.ContentLength.Value + downloaded;
        }

        var fileMode = File.Exists(path) ? FileMode.Open : FileMode.Create;
        using var fs = new FileStream(path, fileMode, FileAccess.Write, FileShare.None, bufferSize: 2 * 1024 * 1024, useAsync: true);
        if (downloaded > 0) fs.Seek(0, SeekOrigin.End);

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[2 * 1024 * 1024];
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            if (read == 0) break;

            await fs.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);

            downloaded += read;

            double speed = sw.Elapsed.TotalSeconds > 0 ? downloaded / sw.Elapsed.TotalSeconds : 0.0;
            double percent = 0.0;

            if (totalSize > 0)
                percent = Math.Min(100.0, Math.Round((downloaded / (double)totalSize) * 100.0, 1));

            string status = BuildStatus(downloaded, totalSize, speed);
            progress?.Report(new DownloadProgress(downloaded, totalSize, speed, percent, status));
        }

        progress?.Report(new DownloadProgress(downloaded, totalSize, 0, 100, "Terminé"));
    }

    private static string BuildStatus(long downloaded, long total, double speed)
    {
        double downMB = Math.Round(downloaded / 1024d / 1024d, 2);
        double speedMB = Math.Round(speed / 1024d / 1024d, 2);

        if (total <= 0)
            return $"{downMB} Mo | {speedMB} Mo/s | taille ? | ???";

        double percent = Math.Min(100.0, Math.Round((downloaded / (double)total) * 100.0, 1));
        double remainingSec = speed > 0 ? (total - downloaded) / speed : 0;

        string timeStr = remainingSec > 0
            ? $"{(int)(remainingSec / 60)}m{(int)(remainingSec % 60):D2}s"
            : "???";

        return $"{downMB} Mo | {speedMB} Mo/s | {percent}% | {timeStr}";
    }
}