using NixManager.ViewModels;
using Runic.Application.Views;

// The Bridge treats a DTO member marked [JsonIgnore], such as Request.IncludesHome, as .NET-only, as System.Text.Json
// does for the stored records.
[assembly: RunicBridgeJsonIgnore]

namespace NixManager.Dashboard;

// The build generates a typed TypeScript client for the Window and each View in Frontend/src/generated.

/// <summary>The dashboard: one Window over the operations of the configuration checkout.</summary>
public sealed partial class DashboardWindow : RunicWindow<DashboardViewModel>;

/// <summary>The running operation, its live log and the history, as <see cref="OperationsViewModel"/> keeps them.</summary>
public sealed partial class OperationsView : RunicView<OperationsViewModel>;
