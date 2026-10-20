using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NixManager.Domain;
using NixManager.Setup;
using Runic.Navigation;

namespace NixManager.ViewModels;

/// <summary>
/// The setup assistant's Window (<see cref="Setup.SetupWindow"/>) before
/// there is a checkout: what was found on this computer, the folder to create, each step's status, and why setup
/// cannot continue or the dashboard could not start.
/// </summary>
/// <remarks>
/// A view of <see cref="SetupAssistantService"/>, whose state changes arrive on its threads and are committed in
/// model-context turns.
/// </remarks>
public sealed partial class SetupViewModel : ObservableObject, IDisposable
{
    private readonly SetupAssistantService _service;
    private readonly IRunicModelContext _context;
    private readonly IDisposable _subscription;

    public SetupViewModel(SetupAssistantService service, IRunicModelContext context)
    {
        _service = service;
        _context = context;
        _subscription = service.Subscribe(state => _context.TryPost(() => State = state));
        State = service.State;
    }

    [ObservableProperty]
    public partial SetupState State { get; private set; }

    /// <summary>
    /// The language the assistant speaks, <c>en</c> or <c>de</c>: the desktop's, since there are no preferences before
    /// setup (<c>locale</c> of <c>SetupState</c>).
    /// </summary>
    public string Locale { get; } = Dashboard.DashboardPreferences.ResolveLocale("system", Environment.GetEnvironmentVariable);

    /// <summary>The folder to create; the suggested one until the user types another.</summary>
    [ObservableProperty]
    public partial string Target { get; set; } = "";

    /// <summary>Why the last request was refused, such as a folder that is not empty.</summary>
    [ObservableProperty]
    public partial DisplayText? RequestError { get; private set; }

    public bool Running => State.Running;

    /// <summary>Setup can start: the computer was read, nothing blocks it and nothing runs.</summary>
    /// <remarks>The dashboard sees it as <see cref="CreateCommand"/>'s availability.</remarks>
    public bool CanCreate => State is { Info.Blocked: null, Running: false, Next: null, Problem: null } && State.Info is not null;

    partial void OnStateChanged(SetupState value)
    {
        if (Target.Length == 0 && value.Info?.Suggested is { } suggested) Target = suggested;
        OnPropertyChanged(nameof(Running));
        OnPropertyChanged(nameof(CanCreate));
        CreateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reads the computer again.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var state = await _service.RefreshAsync().ConfigureAwait(false);
            _context.TryPost(() =>
            {
                State = state;
                RequestError = null;
            });
        }
        catch (ManagerException error)
        {
            _context.TryPost(() => RequestError = error.Text);
        }
    }

    /// <summary>Creates the checkout in <see cref="Target"/>; progress arrives in <see cref="State"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        try
        {
            State = _service.Create(Target.Trim());
            RequestError = null;
        }
        catch (ManagerException error)
        {
            RequestError = error.Text;
        }
    }

    public void Dispose() => _subscription.Dispose();
}
