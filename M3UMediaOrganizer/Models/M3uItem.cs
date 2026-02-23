using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace M3UMediaOrganizer.Models;

public sealed class M3uItem : INotifyPropertyChanged
{
    bool _selected;
    bool _exists;
    string _targetPath = "";
    string _status = "";
    string _searchHay = "";

    public bool Selected { get => _selected; set { _selected = value; OnPropertyChanged(); } }

    public string MediaType { get; set; } = "";     // Film / Serie / Autre
    public string GroupTitle { get; set; } = "";
    public string Title { get; set; } = "";
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string Ext { get; set; } = "";
    public string SourceUrl { get; set; } = "";

    public bool Exists { get => _exists; set { _exists = value; OnPropertyChanged(); } }
    public string TargetPath { get => _targetPath; set { _targetPath = value; OnPropertyChanged(); } }
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    public string SearchHay
    {
        get => _searchHay;
        set { _searchHay = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}