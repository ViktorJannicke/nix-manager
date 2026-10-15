using NixManager.Domain;
using NixManager.Operations;

namespace NixManager.Reviews;

/// <summary>
/// The prepared reviews and the change history of one checkout, for the dashboard and the daemon (the
/// <c>reviews</c> of the dashboard state, <c>/api/review-remove</c> and <c>/api/history</c> in
/// <c>src/server.ts</c>). Preparing, applying, undoing and holding run through <see cref="IOperationService"/>.
/// </summary>
public interface IReviewService
{
    /// <summary>Every prepared review, newest first.</summary>
    IReadOnlyList<BuildReview> Reviews();

    /// <exception cref="ManagerException">The ID is invalid, or the review is unavailable.</exception>
    BuildReview Get(string id);

    /// <summary>Discards a review and its GC roots.</summary>
    /// <exception cref="ManagerException">The ID is invalid, or the running operation uses the review.</exception>
    void Remove(string id);

    /// <summary>The newest commits of the configuration, those Nix Manager made marked as applied.</summary>
    Task<IReadOnlyList<HistoryEntry>> ChangeHistoryAsync(CancellationToken cancellationToken = default);
}

public sealed class ReviewService(IOperationService operations) : IReviewService
{
    private Config Config => operations.Config;

    public IReadOnlyList<BuildReview> Reviews() => ReviewStore.List(Config);

    public BuildReview Get(string id) => ReviewStore.Get(Config, id);

    public void Remove(string id)
    {
        if (operations.Active?.Request.ReviewId == id) throw new ManagerException("This review is in use by the running operation.");
        ReviewStore.Remove(Config, id);
    }

    public Task<IReadOnlyList<HistoryEntry>> ChangeHistoryAsync(CancellationToken cancellationToken = default) =>
        ChangeHistory.ReadAsync(Config.Flake, cancellationToken: cancellationToken);
}
