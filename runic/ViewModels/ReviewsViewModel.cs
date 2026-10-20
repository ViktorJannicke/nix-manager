using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NixManager.Domain;
using NixManager.Operations;
using NixManager.Reviews;
using Runic.Navigation;

namespace NixManager.ViewModels;

/// <summary>
/// The review workflow as the dashboard shows it: the prepared reviews, the change history, and commands to
/// prepare, apply, undo, hold and discard changes.
/// </summary>
/// <remarks>
/// Like <see cref="OperationsViewModel"/>, this is a view of the services: operations run in
/// <see cref="IOperationService"/> beyond this ViewModel, and their events, committed in model-context turns with
/// <see cref="IRunicModelContext.TryPost"/>, refresh the reviews once an operation ends.
/// </remarks>
public sealed partial class ReviewsViewModel : ObservableObject, IDisposable
{
    private readonly IOperationService _operations;
    private readonly IReviewService _reviews;
    private readonly IRunicModelContext _context;
    private readonly IDisposable _subscription;

    public ReviewsViewModel(IOperationService operations, IReviewService reviews, IRunicModelContext context)
    {
        _operations = operations;
        _reviews = reviews;
        _context = context;
        _subscription = operations.Subscribe(OnEvent);
        Reviews = reviews.Reviews();
    }

    /// <summary>The prepared reviews, newest first; reviews prepared by earlier versions included.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<BuildReview> Reviews { get; private set; }

    /// <summary>The newest commits of the configuration; <see cref="RefreshHistoryCommand"/> reads them.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HistoryEntry> ChangeHistory { get; private set; } = [];

    /// <summary>Why the last command failed, such as a review in use or another operation running.</summary>
    [ObservableProperty]
    public partial DisplayText? Error { get; private set; }

    /// <summary>The operation the last prepare, apply or undo started.</summary>
    [ObservableProperty]
    public partial Operation? Started { get; private set; }

    /// <summary>Prepares a review: pending edits, input updates, a restore point or the checkout as it is.</summary>
    [RelayCommand]
    private void Prepare(Request request) =>
        Run(request with { Action = Actions.ReviewBuild, Host = request.Host is { Length: > 0 } ? request.Host : _operations.Config.HostKey });

    /// <summary>Applies a prepared review, now or on next boot.</summary>
    [RelayCommand]
    private void Apply(ApplyReview apply)
    {
        var review = Reviews.FirstOrDefault(item => item.Id == apply.ReviewId);
        Run(Request.Create() with
        {
            Action = Actions.ReviewApply,
            ReviewId = apply.ReviewId,
            Scope = apply.Scope ?? review?.Scope ?? Scopes.All,
            Host = review?.Host is { Length: > 0 } host ? host : _operations.Config.HostKey,
            Activation = apply.Activation,
        });
    }

    /// <summary>Prepares going back from the change an operation applied, for review.</summary>
    [RelayCommand]
    private void Undo(string operationId) => Run(Request.Create() with { Action = Actions.ReviewBuild, Undo = operationId, Host = _operations.Config.HostKey });

    /// <summary>Keeps edits being prepared in one step from being applied, so they wait for review.</summary>
    [RelayCommand]
    private void Hold(string operationId) =>
        Error = _operations.Hold(operationId) ? null : DisplayText.Of("stored_those_changes_are_no_longer_being_prepared");

    /// <summary>Discards a review and its GC roots.</summary>
    [RelayCommand]
    private void Remove(string reviewId)
    {
        try
        {
            _reviews.Remove(reviewId);
            Error = null;
        }
        catch (ManagerException error)
        {
            Error = error.Text;
        }
        Reviews = _reviews.Reviews();
    }

    [RelayCommand]
    private void Refresh() => Reviews = _reviews.Reviews();

    /// <summary>Reads the change history again.</summary>
    [RelayCommand]
    private async Task RefreshHistoryAsync(CancellationToken cancellationToken) =>
        ChangeHistory = await _reviews.ChangeHistoryAsync(cancellationToken).ConfigureAwait(true);

    private void Run(Request request)
    {
        try
        {
            Started = _operations.Start(request).Operation;
            Error = null;
        }
        catch (ManagerException error)
        {
            Error = error.Text;
        }
    }

    private void OnEvent(ManagerEvent e)
    {
        // Reviews change when an operation that prepares or applies one ends.
        if (e is OperationChanged { Operation: { Status: not OperationStatuses.Running } operation } && Actions.IsReview(operation.Request.Action))
            _context.TryPost(() => Reviews = _reviews.Reviews());
    }

    public void Dispose() => _subscription.Dispose();
}

/// <summary>What <see cref="ReviewsViewModel.ApplyCommand"/> applies.</summary>
/// <param name="Scope">The configurations to apply; the review's own scope when null.</param>
/// <param name="Activation"><c>switch</c> (the default) or <c>boot</c>.</param>
public sealed record ApplyReview(string ReviewId, string? Scope = null, string? Activation = null);
