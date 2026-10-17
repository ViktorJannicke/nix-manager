using NixManager.Domain;
using NixManager.Software;
using Runic.Desktop;

namespace NixManager.Dashboard;

/// <summary>
/// App icons for the dashboard's page at <c>app-icon/&lt;id&gt;</c> beside its own files (<c>/api/app-icon/&lt;id&gt;</c>
/// in the Bun version): Flathub's icon of an app in the catalog, from the icon cache or downloaded into it. The
/// Desktop host asks for what the page's build has no file for (<see cref="Runic.Application.Views.Desktop.RunicDesktopHost.Resources"/>),
/// in a native window and in a browser alike.
/// </summary>
/// <remarks>
/// An unknown id, an app without an icon, a file larger than <see cref="MaxBytes"/> and app details turned off are
/// 404, and the page shows the app's initial instead. At most six icons download at once.
/// </remarks>
public sealed class AppIconResources(ISoftwareService service) : IDisposable
{
    /// <summary>The path prefix of an icon, below the page's own address.</summary>
    public const string Prefix = "/app-icon/";

    /// <summary>A larger file is not an app icon of Flathub's; it is not served.</summary>
    public const int MaxBytes = 256 * 1024;

    private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>
    {
        // An app's icon does not change within a session.
        ["Cache-Control"] = "private, max-age=86400",
    };

    private readonly SemaphoreSlim _downloads = new(6);

    /// <summary>The Desktop host's handler for a dashboard Window.</summary>
    public ValueTask<ContentResponse?> ServeAsync(ContentRequest request, CancellationToken cancellationToken) =>
        ServeAsync(request.Method, request.Path, cancellationToken);

    /// <summary>The icon for a <c>GET</c> or <c>HEAD</c> of <paramref name="path"/>, or null for 404.</summary>
    public async ValueTask<ContentResponse?> ServeAsync(string method, string path, CancellationToken cancellationToken)
    {
        if (method is not ("GET" or "HEAD") || !path.StartsWith(Prefix, StringComparison.Ordinal) || !service.AppDetails) return null;
        // The path is decoded and could name anything; only an AppStream id is looked up, never a file.
        var id = path[Prefix.Length..];
        if (!Apps.ValidAppId(id)) return null;
        await _downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? png;
        try
        {
            png = await service.IconAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ManagerException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _downloads.Release();
        }
        return png is { Length: > 0 and <= MaxBytes } ? new ContentResponse(png, "image/png", headers: Headers) : null;
    }

    public void Dispose() => _downloads.Dispose();
}
