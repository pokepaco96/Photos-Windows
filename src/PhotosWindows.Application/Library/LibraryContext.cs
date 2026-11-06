using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.Application.Library;

/// <summary>Holds the currently opened library. Set only by <see cref="LibraryManager"/>.</summary>
public sealed class LibraryContext : ILibraryContext
{
    private LibraryInfo? _current;

    public LibraryInfo? Current => _current;

    public event EventHandler<LibraryInfo?>? LibraryChanged;

    public LibraryInfo RequireCurrent() => _current ?? throw new InvalidOperationException("No library is open.");

    internal void Set(LibraryInfo? library)
    {
        _current = library;
        LibraryChanged?.Invoke(this, library);
    }
}
