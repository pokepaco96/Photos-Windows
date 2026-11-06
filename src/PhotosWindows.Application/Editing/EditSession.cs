using PhotosWindows.Domain;

namespace PhotosWindows.Application.Editing;

/// <summary>Undo/redo history for one editing session. Consecutive changes of the same control can be coalesced.</summary>
public sealed class EditSession
{
    private const int MaxHistory = 100;
    private readonly List<EditRecipe> _undo = [];
    private readonly List<EditRecipe> _redo = [];
    private string? _lastCoalesceKey;

    public EditSession(long assetId, EditRecipe initial)
    {
        AssetId = assetId;
        Original = initial;
        Current = initial;
    }

    public long AssetId { get; }

    /// <summary>Recipe stored when the session started (used for "Revert to saved").</summary>
    public EditRecipe Original { get; }

    public EditRecipe Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public bool IsDirty => Current != Original;

    /// <param name="coalesceKey">Changes with the same key in a row (dragging one slider) form a single undo step.</param>
    public void Apply(EditRecipe next, string? coalesceKey = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        var normalized = next.Normalize();
        if (normalized == Current) return;
        if (coalesceKey is null || coalesceKey != _lastCoalesceKey || _undo.Count == 0)
        {
            _undo.Add(Current);
            if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
        }

        _lastCoalesceKey = coalesceKey;
        _redo.Clear();
        Current = normalized;
    }

    public void EndGesture() => _lastCoalesceKey = null;

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Add(Current);
        Current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _lastCoalesceKey = null;
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(Current);
        Current = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _lastCoalesceKey = null;
        return true;
    }
}
