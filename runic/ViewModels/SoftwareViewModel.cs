using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NixManager.Domain;
using NixManager.Edits;
using NixManager.Software;
using Runic.Navigation;

namespace NixManager.ViewModels;

/// <summary>
/// The Software page of one checkout: the pending draft, what the selected host installs, the package catalog with
/// its search, and commands to change the draft. A view of <see cref="ISoftwareService"/>, which caches evaluations
/// by the checkout state; the page passes the fingerprint and lock hash it observed (W300-030 binds it).
/// </summary>
public sealed partial class SoftwareViewModel : ObservableObject, IDisposable
{
    private readonly ISoftwareService _service;
    private readonly IRunicModelContext _context;

    public SoftwareViewModel(ISoftwareService service, IRunicModelContext context)
    {
        _service = service;
        _context = context;
        _service.Changed += OnChanged;
    }

    /// <summary>The host whose configurations are shown; empty for this computer's.</summary>
    [ObservableProperty]
    public partial string Host { get; set; } = "";

    /// <summary>The checkout state the inventory and the draft were read from.</summary>
    [ObservableProperty]
    public partial string Fingerprint { get; set; } = "";

    /// <summary>Hash of the checkout's flake.lock, which selects the package catalog.</summary>
    [ObservableProperty]
    public partial string? LockHash { get; set; }

    [ObservableProperty]
    public partial RenderedDraft? Draft { get; private set; }

    [ObservableProperty]
    public partial SoftwareInventory? Inventory { get; private set; }

    [ObservableProperty]
    public partial CatalogStatus Catalog { get; private set; } = new() { State = "missing" };

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool FreeOnly { get; set; }

    [ObservableProperty]
    public partial bool WorkingOnly { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<AppEntry> Results { get; private set; } = [];

    /// <summary>Why the last read or change failed.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    partial void OnQueryChanged(string value) => Search();

    partial void OnFreeOnlyChanged(bool value) => Search();

    partial void OnWorkingOnlyChanged(bool value) => Search();

    /// <summary>Reads the draft, the catalog status and the host's inventory for the current checkout state.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            Error = null;
            Catalog = _service.CatalogStatus(LockHash);
            Draft = await _service.DraftAsync(Fingerprint);
            Inventory = await _service.InventoryAsync(Host, Fingerprint);
        }
        catch (ManagerException error)
        {
            Error = error.Message;
        }
    }

    /// <summary>Builds the package catalog of the current lock file, or again with <paramref name="rebuild"/>.</summary>
    [RelayCommand]
    private void BuildCatalog(bool rebuild)
    {
        if (LockHash is not { Length: > 0 } hash)
        {
            Error = "The checkout has not been read yet.";
            return;
        }
        _service.BuildCatalog(hash, rebuild);
        Catalog = _service.CatalogStatus(hash);
    }

    [RelayCommand]
    private void Search() => Results = _service.Search(LockHash, Query, new CatalogFilter(FreeOnly, WorkingOnly));

    /// <summary>Replaces the draft with the page's edits.</summary>
    [RelayCommand]
    private async Task SetDraftAsync(IReadOnlyList<Change> changes)
    {
        try
        {
            Error = null;
            Draft = await _service.SetDraftAsync(new JsonArray([.. changes.Select(change => (JsonNode)change.ToJson())]));
        }
        catch (ManagerException error)
        {
            Error = error.Message;
        }
    }

    private void OnChanged() => _context.TryPost(() =>
    {
        Catalog = _service.CatalogStatus(LockHash);
        Search();
    });

    public void Dispose() => _service.Changed -= OnChanged;
}
