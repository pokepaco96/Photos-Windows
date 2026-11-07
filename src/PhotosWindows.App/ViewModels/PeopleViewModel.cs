using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using PhotosWindows.App.Services;
using PhotosWindows.Domain;

namespace PhotosWindows.App.ViewModels;

/// <summary>A person (group of faces) card. Names are only ever the user's; otherwise "Unidentified person".</summary>
public sealed partial class PersonCardViewModel : ObservableObject
{
    public PersonCardViewModel(Person person)
    {
        Person = person;
        Name = PeopleCoordinator.DisplayName(person);
        Subtitle = Loc.Format("People_PhotoCount", person.AssetCount);
        AutomationName = $"{Name}, {Subtitle}";
    }

    public Person Person { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public string AutomationName { get; }
    public bool IsNamed => Person.IsNamed;

    [ObservableProperty] public partial ImageSource? Face { get; set; }
}

/// <summary>A face that is not in any group yet.</summary>
public sealed partial class FaceTileViewModel(Face face) : ObservableObject
{
    public Face Face { get; } = face;

    [ObservableProperty] public partial ImageSource? Image { get; set; }
}

public sealed partial class PeopleViewModel(PeopleCoordinator coordinator, NavigationService navigation, UiThread ui) : ObservableObject, IDisposable
{
    private bool _subscribed;

    public ObservableCollection<PersonCardViewModel> People { get; } = [];
    public ObservableCollection<FaceTileViewModel> Ungrouped { get; } = [];

    public IReadOnlyList<PersonCardViewModel> Selected { get; set; } = [];

    [ObservableProperty] public partial string Stats { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasPending { get; set; }
    [ObservableProperty] public partial string AnalyzeText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string EmptyTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EmptyText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasUngrouped { get; set; }
    [ObservableProperty] public partial string UngroupedTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CanMerge { get; set; }
    [ObservableProperty] public partial bool CanRename { get; set; }
    [ObservableProperty] public partial bool IsAvailable { get; set; }

    public async Task LoadAsync()
    {
        if (!_subscribed)
        {
            coordinator.People.Changed += OnChanged;
            _subscribed = true;
        }

        IsAvailable = await coordinator.IsAvailableAsync();
        if (!IsAvailable)
        {
            People.Clear();
            Ungrouped.Clear();
            IsEmpty = true;
            EmptyTitle = Loc.Get("People_NotSetUpTitle");
            EmptyText = Loc.Get("People_NotSetUpText");
            return;
        }

        var stats = await coordinator.People.GetStatsAsync(CancellationToken.None);
        Stats = PeopleText.Stats(stats);
        HasPending = stats.Pending > 0;
        AnalyzeText = Loc.Format("People_AnalyzePending", stats.Pending);

        var people = await coordinator.People.GetPeopleAsync(CancellationToken.None);
        People.Clear();
        foreach (var person in people)
        {
            var card = new PersonCardViewModel(person);
            People.Add(card);
            _ = LoadFaceAsync(card);
        }

        var ungrouped = await coordinator.People.GetUngroupedFacesAsync(200, CancellationToken.None);
        Ungrouped.Clear();
        foreach (var face in ungrouped)
        {
            var tile = new FaceTileViewModel(face);
            Ungrouped.Add(tile);
            _ = LoadFaceAsync(tile);
        }

        HasUngrouped = Ungrouped.Count > 0;
        UngroupedTitle = Loc.Format("People_UngroupedTitle", stats.Ungrouped);
        IsEmpty = People.Count == 0;
        (EmptyTitle, EmptyText) = stats.Analyzed == 0
            ? (Loc.Get("People_NotAnalyzedTitle"), Loc.Get("People_NotAnalyzedText"))
            : (Loc.Get("People_NoneFoundTitle"), Loc.Get("People_NoneFoundText"));
        UpdateSelection([]);
    }

    public void UpdateSelection(IReadOnlyList<PersonCardViewModel> selected)
    {
        Selected = selected;
        CanMerge = selected.Count >= 2;
        CanRename = selected.Count == 1;
    }

    public void Open(PersonCardViewModel card) => navigation.ToPerson(card.Person.Id);

    [RelayCommand]
    private Task AnalyzeAsync() => coordinator.AnalyzeAsync();

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (Selected is [var card]) await coordinator.RenameAsync(card.Person);
    }

    /// <summary>Merges the selected groups: the user picks which one to keep (named and larger groups are offered first).</summary>
    [RelayCommand]
    private async Task MergeAsync()
    {
        if (Selected.Count < 2) return;
        var ordered = Selected.OrderByDescending(c => c.IsNamed).ThenByDescending(c => c.Person.AssetCount).Select(c => c.Person).ToList();
        var keep = await coordinator.PickPersonAsync(ordered, Loc.Format("People_MergeSelectedTitle", Selected.Count), Loc.Get("People_MergeButton"));
        if (keep is null) return;
        await coordinator.People.MergeAsync(ordered.Select(p => p.Id).ToList(), keep.Id, CancellationToken.None);
    }

    [RelayCommand]
    private void OpenSettings() => navigation.ToSettings();

    public async Task NameFaceAsync(FaceTileViewModel tile)
    {
        var id = await coordinator.People.MoveFacesAsync([tile.Face.Id], null, CancellationToken.None);
        var person = await coordinator.People.GetPersonAsync(id, CancellationToken.None);
        if (person is not null) await coordinator.RenameAsync(person);
    }

    public async Task AddFaceToPersonAsync(FaceTileViewModel tile)
    {
        var target = await coordinator.PickPersonAsync(await coordinator.People.GetPeopleAsync(CancellationToken.None), Loc.Get("People_AddToPersonTitle"), Loc.Get("Common_Add"));
        if (target is not null) await coordinator.People.MoveFacesAsync([tile.Face.Id], target.Id, CancellationToken.None);
    }

    public Task IgnoreFaceAsync(FaceTileViewModel tile) => coordinator.People.IgnoreFacesAsync([tile.Face.Id], CancellationToken.None);

    public Task RenamePersonAsync(PersonCardViewModel card) => coordinator.RenameAsync(card.Person);

    public Task DeletePersonAsync(PersonCardViewModel card) => coordinator.DeleteAsync(card.Person);

    public void Dispose()
    {
        if (_subscribed) coordinator.People.Changed -= OnChanged;
        _subscribed = false;
    }

    private void OnChanged(object? sender, EventArgs e) => ui.Post(() => _ = LoadAsync());

    private async Task LoadFaceAsync(PersonCardViewModel card)
    {
        var faceId = card.Person.CoverFaceId ?? (await coordinator.People.GetFacesOfPersonAsync(card.Person.Id, CancellationToken.None)).FirstOrDefault()?.Id;
        if (faceId is { } id) card.Face = await coordinator.LoadFaceImageAsync(id);
    }

    private async Task LoadFaceAsync(FaceTileViewModel tile) => tile.Image = await coordinator.LoadFaceImageAsync(tile.Face.Id, 96);
}
