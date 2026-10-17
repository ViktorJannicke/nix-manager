using NixManager.ViewModels;
using Runic.Application.Views;

namespace NixManager.Dashboard;

// The Software page's child View: the app list, the large part of SoftwareViewModel, gets snapshots of its own, so a
// keystroke in the search does not resend it.

/// <summary>Every package Flathub describes as an app (<see cref="AppListViewModel"/>).</summary>
public sealed partial class AppListView : RunicView<AppListViewModel>;
