using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

using WinForms = System.Windows.Forms;
using Win32 = Microsoft.Win32;
using WpfMessageBox = System.Windows.MessageBox;

using M3UMediaOrganizer.Models;
using M3UMediaOrganizer.Services;

namespace M3UMediaOrganizer;

public partial class MainWindow : Window
{
    readonly ObservableCollection<M3uItem> _allItems = new();
    readonly ICollectionView _view;

    readonly M3uParser _parser = new();
    readonly TiviMateDownloader _downloader = new();
    static readonly HttpClient _http = CreateM3uHttpClient();

    HashSet<string>? _existingIndex;
    string _root = "";
    string _m3uPath = "";
    string _searchText = "";

    CancellationTokenSource? _downloadCts;
    bool _isDownloading;

    // throttle recherche
    readonly System.Windows.Threading.DispatcherTimer _searchTimer;

    private static HttpClient CreateM3uHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        return client;
    }

    public MainWindow()
    {
        InitializeComponent();

        _view = CollectionViewSource.GetDefaultView(_allItems);
        GridItems.ItemsSource = _view;

        _view.Filter = FilterItem;

        _searchTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _view.Refresh();
        };

        RebuildFilters();
    }

    // -------------------------
    // UI: Dialogs
    // -------------------------
    private void BtnPickRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Sélectionnez le dossier Root de téléchargement"
        };

        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;

        _root = dlg.SelectedPath;
        TxtRoot.Text = _root;

        LblM3UProgress.Text = "Indexation fichiers existants...";
        DoEventsRender();

        _existingIndex = ExistingIndex.Build(_root);

        LblM3UProgress.Text = $"Indexation OK : {_existingIndex.Count} fichiers";
        RefreshComputedFields();
        _view.Refresh();
        RebuildFilters();
    }

    private async void BtnOpenM3U_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Win32.OpenFileDialog
        {
            Filter = "Playlist M3U (*.m3u;*.m3u8)|*.m3u;*.m3u8|Tous les fichiers (*.*)|*.*",
            Title = "Sélectionner un fichier M3U"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _m3uPath = dlg.FileName;
            TxtM3UPath.Text = _m3uPath;
            await LoadM3uFromFileAsync(_m3uPath);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Impossible de parser le M3U.\n\n{ex.Message}", "Erreur M3U", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnLoadM3UUrl_Click(object sender, RoutedEventArgs e)
    {
        var rawUrl = (TxtM3UUrl.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            WpfMessageBox.Show("Saisis une URL M3U valide.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            WpfMessageBox.Show("URL invalide (http/https attendu).", "Erreur URL", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        BtnOpenM3U.IsEnabled = false;
        BtnLoadM3UUrl.IsEnabled = false;

        string? tempFile = null;
        try
        {
            tempFile = Path.Combine(Path.GetTempPath(), $"m3u_{Guid.NewGuid():N}.m3u8");
            await DownloadM3uToFileAsync(uri, tempFile);
            TxtM3UPath.Text = $"{uri} (temp)";
            await LoadM3uFromFileAsync(tempFile, showSuccessMessage: true);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Impossible de charger le M3U via URL.\n\n{ex.Message}", "Erreur URL M3U", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnOpenM3U.IsEnabled = true;
            BtnLoadM3UUrl.IsEnabled = true;
            if (!string.IsNullOrWhiteSpace(tempFile) && File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    private async Task DownloadM3uToFileAsync(Uri uri, string destinationPath)
    {
        PbM3U.IsIndeterminate = false;
        PbM3U.Value = 0;

        const int maxAttempts = 3;
        Exception? lastError = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                LblM3UProgress.Text = $"Téléchargement M3U URL : tentative {attempt}/{maxAttempts}...";

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", "ExoPlayer/2.19.1");
                request.Headers.TryAddWithoutValidation("Accept", "application/vnd.apple.mpegurl, */*");
                request.Headers.Referrer = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
                request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
                request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                request.Headers.TryAddWithoutValidation("Pragma", "no-cache");

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true);

                var buffer = new byte[1024 * 64];
                long read = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await output.WriteAsync(buffer, 0, n);
                    read += n;

                    if (total.GetValueOrDefault() > 0)
                    {
                        int percent = (int)Math.Min(100, Math.Round(read / (double)total.Value * 100, 0));
                        PbM3U.IsIndeterminate = false;
                        PbM3U.Value = percent;
                        LblM3UProgress.Text = $"Téléchargement M3U URL : tentative {attempt}/{maxAttempts} | {percent}% | {read}/{total.Value} bytes";
                    }
                    else
                    {
                        PbM3U.IsIndeterminate = true;
                        LblM3UProgress.Text = $"Téléchargement M3U URL : tentative {attempt}/{maxAttempts} | {read} bytes";
                    }
                }

                var writtenBytes = new FileInfo(destinationPath).Length;
                if (writtenBytes < 100)
                    throw new InvalidDataException("Playlist vide ou invalide (moins de 100 bytes).");

                PbM3U.IsIndeterminate = false;
                PbM3U.Value = 100;
                LblM3UProgress.Text = $"Téléchargement M3U URL : OK ({writtenBytes} bytes)";
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= maxAttempts)
                    break;

                LblM3UProgress.Text = $"Téléchargement M3U URL : échec tentative {attempt}/{maxAttempts} ({ex.Message}). Nouvelle tentative...";
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        throw new HttpRequestException("Impossible de télécharger la playlist M3U après 3 tentatives.", lastError);
    }

    private async Task LoadM3uFromFileAsync(string path, bool showSuccessMessage = true)
    {
        BtnOpenM3U.IsEnabled = false;
        BtnLoadM3UUrl.IsEnabled = false;

        PbM3U.IsIndeterminate = false;
        PbM3U.Value = 0;
        LblM3UProgress.Text = "Chargement M3U : démarrage...";
        DoEventsRender();

        try
        {
            var prog = new Progress<M3uParser.ParseProgress>(p =>
            {
                PbM3U.Value = p.Percent;
                LblM3UProgress.Text = $"Chargement M3U : {p.Percent}% | {p.ReadBytes}/{p.TotalBytes} bytes | items={p.ItemsCount}";
            });

            using var cts = new CancellationTokenSource();
            var items = await _parser.ParseByBytesAsync(path, prog, cts.Token);

            _allItems.Clear();
            foreach (var it in items)
                _allItems.Add(it);

            if (!string.IsNullOrWhiteSpace(_root))
            {
                _existingIndex ??= ExistingIndex.Build(_root);
                RefreshComputedFields();
            }

            PbM3U.IsIndeterminate = false;
            PbM3U.Value = 100;
            LblM3UProgress.Text = $"Chargement M3U : terminé | Items: {_allItems.Count}";

            RebuildFilters();
            _view.Refresh();

            if (showSuccessMessage)
                WpfMessageBox.Show($"Chargé : {_allItems.Count} entrées", "M3U", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            BtnOpenM3U.IsEnabled = true;
            BtnLoadM3UUrl.IsEnabled = true;
        }
    }

    // -------------------------
    // Filtrage / Recherche
    // -------------------------
    private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _searchText = (TxtSearch.Text ?? "").Trim().ToLowerInvariant();
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void CmbType_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
    }

    private void CmbGenre_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
    }

    private void CmbSeason_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
    }

    private void CmbExt_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
    }

    private void ChkHideExisting_Click(object sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
    }

    private bool FilterItem(object obj)
    {
        if (obj is not M3uItem it) return false;

        bool hideExisting = ChkHideExisting.IsChecked == true;
        if (hideExisting && it.Exists) return false;

        string typeFilter = GetSelectedComboText(CmbType, "Tout afficher");
        switch (typeFilter)
        {
            case "Films + Séries":
                if (it.MediaType is not ("Film" or "Serie")) return false;
                break;
            case "Séries":
                if (it.MediaType != "Serie") return false;
                break;
            case "Films":
                if (it.MediaType != "Film") return false;
                break;
        }

        string genreFilter = GetSelectedComboText(CmbGenre, "Tous genres");
        if (!string.IsNullOrWhiteSpace(genreFilter) && genreFilter != "Tous genres")
        {
            if (genreFilter == "(Sans genre)")
            {
                if (!string.IsNullOrWhiteSpace(it.GroupTitle)) return false;
            }
            else
            {
                if (!string.Equals(it.GroupTitle ?? "", genreFilter, StringComparison.Ordinal)) return false;
            }
        }

        string seasonFilter = GetSelectedComboText(CmbSeason, "Toutes saisons");
        if (seasonFilter != "Toutes saisons")
        {
            if (seasonFilter == "(Sans saison)")
            {
                if (it.Season.HasValue) return false;
            }
            else
            {
                if (!int.TryParse(seasonFilter, out int selectedSeason) || it.Season != selectedSeason)
                    return false;
            }
        }

        string extFilter = GetSelectedComboText(CmbExt, "Toutes");
        if (extFilter != "Toutes")
        {
            if (extFilter == "(Sans extension)")
            {
                if (!string.IsNullOrWhiteSpace(it.Ext)) return false;
            }
            else if (!string.Equals((it.Ext ?? ""), extFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            if (string.IsNullOrWhiteSpace(it.SearchHay))
                it.SearchHay = ((it.Title + " " + it.GroupTitle + " " + it.SourceUrl).ToLowerInvariant());

            if (!it.SearchHay.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    // -------------------------
    // Computed fields
    // -------------------------
    private void RefreshComputedFields()
    {
        if (string.IsNullOrWhiteSpace(_root))
        {
            foreach (var it in _allItems)
            {
                it.TargetPath = "";
                it.Exists = false;
                if (it.Status == "Déjà là") it.Status = "";
            }
            return;
        }

        foreach (var it in _allItems)
        {
            if (it.MediaType is "Film" or "Serie")
            {
                it.TargetPath = PathPlanner.GetTargetPathFromItem(it, _root);

                bool exists = false;
                if (_existingIndex is not null && !string.IsNullOrWhiteSpace(it.TargetPath))
                    exists = _existingIndex.Contains(ExistingIndex.NormalizeFullPath(it.TargetPath));
                else
                    exists = File.Exists(it.TargetPath);

                it.Exists = exists;
                if (exists) it.Status = "Déjà là";
                else if (it.Status == "Déjà là") it.Status = "";
            }
            else
            {
                it.TargetPath = "";
                it.Exists = false;
                if (it.Status == "Déjà là") it.Status = "";
            }
        }
    }

    private void BtnRecalc_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_root))
        {
            WpfMessageBox.Show("Choisis d'abord le dossier Root.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LblM3UProgress.Text = "Indexation fichiers existants...";
        DoEventsRender();

        _existingIndex = ExistingIndex.Build(_root);

        LblM3UProgress.Text = $"Indexation OK : {_existingIndex.Count} fichiers";
        RefreshComputedFields();
        RebuildFilters();
        _view.Refresh();
        GridItems.Items.Refresh();
    }

    // -------------------------
    // Sélection DataGrid
    // -------------------------
    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        using (_view.DeferRefresh())
        {
            foreach (var it in _view.Cast<M3uItem>())
                it.Selected = true;
        }
    }

    private void BtnSelectOnly_Click(object sender, RoutedEventArgs e)
    {
        var sel = GridItems.SelectedItems.Cast<M3uItem>().ToArray();
        if (sel.Length == 0) return;

        using (_view.DeferRefresh())
        {
            foreach (var it in sel)
                it.Selected = true;
        }
    }

    private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
    {
        using (_view.DeferRefresh())
        {
            foreach (var it in _view.Cast<M3uItem>())
                it.Selected = false;
        }
    }

    // -------------------------
    // Download
    // -------------------------
    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        BtnCancel.IsEnabled = false;
    }

    private async void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;

        if (string.IsNullOrWhiteSpace(_root))
        {
            WpfMessageBox.Show("Choisis d'abord le dossier Root.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var toDownload = _allItems.Where(x => x.Selected && (x.MediaType is "Film" or "Serie")).ToList();
        if (toDownload.Count == 0)
        {
            WpfMessageBox.Show("Aucun Film/Série sélectionné.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        const int delayBetweenFilesSeconds = 15;

        _isDownloading = true;
        BtnDownload.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        _downloadCts = new CancellationTokenSource();

        PbDlFile.Value = 0;
        PbDlAll.Value = 0;
        LblDlFile.Text = "Téléchargement fichier : préparation...";
        LblDlAll.Text = $"Téléchargement global : 0/{toDownload.Count}";

        try
        {
            for (int i = 0; i < toDownload.Count; i++)
            {
                _downloadCts.Token.ThrowIfCancellationRequested();

                var it = toDownload[i];
                it.Status = "Préparation...";
                GridItems.Items.Refresh();

                LblDlFile.Text = $"Téléchargement fichier : {it.Title} ({i + 1}/{toDownload.Count})";
                PbDlFile.Value = 0;

                var targetPath = it.TargetPath;
                if (string.IsNullOrWhiteSpace(targetPath))
                    targetPath = PathPlanner.GetTargetPathFromItem(it, _root);

                var prog = new Progress<TiviMateDownloader.DownloadProgress>(p =>
                {
                    // throttle léger côté UI
                    PbDlFile.Value = p.Percent;
                    it.Status = p.Status;
                });

                try
                {
                    await _downloader.DownloadOneAsync(it.SourceUrl, targetPath, prog, _downloadCts.Token);

                    it.TargetPath = targetPath;
                    it.Status = "Terminé";
                    it.Exists = true;

                    _existingIndex ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _existingIndex.Add(ExistingIndex.NormalizeFullPath(targetPath));
                }
                catch (OperationCanceledException)
                {
                    it.Status = "Annulé";
                    throw;
                }
                catch (Exception ex)
                {
                    it.Status = "Erreur: " + ex.Message;
                }

                int done = i + 1;
                int globalPercent = (int)Math.Round(done / (double)toDownload.Count * 100.0, 0);
                PbDlAll.Value = globalPercent;
                LblDlAll.Text = $"Téléchargement global : {done}/{toDownload.Count} ({globalPercent}%)";

                GridItems.Items.Refresh();

                if (delayBetweenFilesSeconds > 0 && i < toDownload.Count - 1)
                    await Task.Delay(TimeSpan.FromSeconds(delayBetweenFilesSeconds), _downloadCts.Token);
            }

            LblDlFile.Text = "Téléchargement fichier : terminé";
        }
        catch (OperationCanceledException)
        {
            LblDlFile.Text = "Téléchargement fichier : annulé";
        }
        finally
        {
            _isDownloading = false;
            BtnDownload.IsEnabled = true;
            BtnCancel.IsEnabled = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    // -------------------------
    // Genres
    // -------------------------
    private void RebuildFilters()
    {
        var current = GetSelectedComboText(CmbGenre, "Tous genres");
        var currentSeason = GetSelectedComboText(CmbSeason, "Toutes saisons");
        var currentExt = GetSelectedComboText(CmbExt, "Toutes");

        CmbGenre.Items.Clear();
        CmbGenre.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Tous genres" });
        CmbGenre.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "(Sans genre)" });

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in _allItems)
            if (!string.IsNullOrWhiteSpace(it.GroupTitle))
                set.Add(it.GroupTitle);

        foreach (var g in set.OrderBy(x => x))
            CmbGenre.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = g });

        // restore
        var found = CmbGenre.Items.OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(x => string.Equals((string?)x.Content, current, StringComparison.Ordinal));

        if (found is not null) CmbGenre.SelectedItem = found;
        else CmbGenre.SelectedIndex = 0;

        CmbSeason.Items.Clear();
        CmbSeason.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Toutes saisons" });
        CmbSeason.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "(Sans saison)" });

        foreach (var season in _allItems.Where(x => x.Season.HasValue).Select(x => x.Season!.Value).Distinct().OrderBy(x => x))
            CmbSeason.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = season.ToString() });

        var foundSeason = CmbSeason.Items.OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(x => string.Equals((string?)x.Content, currentSeason, StringComparison.Ordinal));
        if (foundSeason is not null) CmbSeason.SelectedItem = foundSeason;
        else CmbSeason.SelectedIndex = 0;

        CmbExt.Items.Clear();
        CmbExt.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Toutes" });
        CmbExt.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "(Sans extension)" });

        foreach (var ext in _allItems.Select(x => x.Ext).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            CmbExt.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = ext });

        var foundExt = CmbExt.Items.OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(x => string.Equals((string?)x.Content, currentExt, StringComparison.Ordinal));
        if (foundExt is not null) CmbExt.SelectedItem = foundExt;
        else CmbExt.SelectedIndex = 0;
    }

    // -------------------------
    // Helpers
    // -------------------------
    private static string GetSelectedComboText(System.Windows.Controls.ComboBox cmb, string fallback)
    {
        if (cmb.SelectedItem is System.Windows.Controls.ComboBoxItem cbi && cbi.Content is not null)
            return cbi.Content.ToString() ?? fallback;
        return fallback;
    }

    private void DoEventsRender()
        => Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

    protected override void OnClosing(CancelEventArgs e)
    {
        _downloadCts?.Cancel();
        base.OnClosing(e);
    }
}
