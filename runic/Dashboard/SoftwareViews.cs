using NixManager.ViewModels;
using Runic.Application.Views;

namespace NixManager.Dashboard;

// The Software page's child Views: the parts of SoftwareViewModel that are large or change often get snapshots of
// their own, so a keystroke in the search does not resend the app list, and a batch of icons not the page.

/// <summary>Every package Flathub describes as an app (<see cref="AppListViewModel"/>).</summary>
public sealed partial class AppListView : RunicView<AppListViewModel>;

/// <summary>App icons as <c>data:</c> URLs (<see cref="AppIconsViewModel"/>).</summary>
public sealed partial class AppIconsView : RunicView<AppIconsViewModel>;
