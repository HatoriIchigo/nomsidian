using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Nomsidian;

public enum DocumentKind
{
    Text,
    Image,
    Pdf,
    Unsupported,
}

public sealed class OpenDocument : INotifyPropertyChanged
{
    private string _filePath;
    private string _text;
    private bool _isDirty;

    public OpenDocument(string filePath, string text, DocumentKind kind = DocumentKind.Text)
    {
        _filePath = filePath;
        _text = text;
        Kind = kind;
    }

    public DocumentKind Kind { get; }

    public string FilePath
    {
        get => _filePath;
        private set
        {
            _filePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string FileName => Path.GetFileName(FilePath);

    public string DisplayName => (IsDirty ? "* " : string.Empty) + FileName;

    public string Text
    {
        get => _text;
        set => _text = value;
    }

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty == value)
            {
                return;
            }

            _isDirty = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public void UpdatePath(string newPath) => FilePath = newPath;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
