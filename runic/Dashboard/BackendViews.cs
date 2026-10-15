using NixManager.ViewModels;
using Runic.Application.Views;

namespace NixManager.Dashboard;

// The backend's ViewModels for the pages the dashboard still shows as placeholders (W300-033). Declaring their
// Views generates typed clients in Frontend/src/generated and keeps the ViewModels bindable: the build fails when
// one of their values cannot cross the Bridge. The pages themselves follow in the dashboard.

/// <summary>Prepared reviews, applying and undoing them, and the change history (<see cref="ReviewsViewModel"/>).</summary>
public sealed partial class ReviewsView : RunicView<ReviewsViewModel>;

/// <summary>Installed software, the package catalogue and the pending edits (<see cref="SoftwareViewModel"/>).</summary>
public sealed partial class SoftwareView : RunicView<SoftwareViewModel>;

/// <summary>Restore points, the checkout's state and cleanup estimates (<see cref="SystemViewModel"/>).</summary>
public sealed partial class SystemView : RunicView<SystemViewModel>;

/// <summary>The setup assistant, before there is a checkout to manage (<see cref="SetupViewModel"/>).</summary>
public sealed partial class SetupView : RunicView<SetupViewModel>;
