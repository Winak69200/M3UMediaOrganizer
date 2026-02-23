using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
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

    HashSet<string>? _existingIndex;
    string _root = "";
    string _m3uPath = "";

    CancellationTokenSource? _downloadCts;
    bool _isDownloading;

    // throttle recherche
    readonly System.Windows.Threading.DispatcherTimer _searchTimer;

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

        RebuildGenreList();
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
        RebuildGenreList();
    }

    private async void BtnOpenM3U_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Win32.OpenFileDialog
        {
            Filter = "Playlist M3U (*.m3u;*.m3u8)|*.m3u;*.m3u8|Tous les fichiers (*.*)|*.*",
            Title = "Sélectionner un fichier M3U"
        };
        if (dlg.ShowDialog() != true) return;

        _m3uPath = dlg.FileName;
        TxtM3UPath.Text = _m3uPath;

        BtnOpenM3U.IsEnabled = false;
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

            // Parse en background
            var items = await _parser.ParseByBytesAsync(_m3uPath, prog, cts.Token);

            _allItems.Clear();
            foreach (var it in items)
                _allItems.Add(it);

            // si root déjà choisi → recalcul + exists
            if (!string.IsNullOrWhiteSpace(_root))
            {
                _existingIndex ??= ExistingIndex.Build(_root);
                RefreshComputedFields();
            }

            PbM3U.Value = 100;
            LblM3UProgress.Text = $"Chargement M3U : terminé | Items: {_allItems.Count}";

            RebuildGenreList();
            _view.Refresh();

            WpfMessageBox.Show($"Chargé : {_allItems.Count} entrées", "M3U", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Impossible de parser le M3U.\n\n{ex.Message}", "Erreur M3U", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnOpenM3U.IsEnabled = true;
        }
    }

    // -------------------------
    // Filtrage / Recherche
    // -------------------------
    private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
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

        var s = (TxtSearch.Text ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(s))
        {
            if (string.IsNullOrWhiteSpace(it.SearchHay))
                it.SearchHay = ((it.Title + " " + it.GroupTitle + " " + it.SourceUrl).ToLowerInvariant());

            if (!it.SearchHay.Contains(s.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
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
        RebuildGenreList();
        _view.Refresh();
        GridItems.Items.Refresh();
    }

    // -------------------------
    // Sélection DataGrid
    // -------------------------
    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in _view.Cast<M3uItem>())
            it.Selected = true;
        _view.Refresh();
    }

    private void BtnSelectOnly_Click(object sender, RoutedEventArgs e)
    {
        var sel = GridItems.SelectedItems.Cast<M3uItem>().ToArray();
        if (sel.Length == 0) return;

        foreach (var it in sel)
            it.Selected = true;

        _view.Refresh();
    }

    private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in _view.Cast<M3uItem>())
            it.Selected = false;
        _view.Refresh();
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
    private void RebuildGenreList()
    {
        var current = GetSelectedComboText(CmbGenre, "Tous genres");

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