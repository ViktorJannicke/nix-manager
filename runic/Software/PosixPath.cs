namespace NixManager.Software;

/// <summary>Node's <c>path.posix</c> functions the software page uses for flake-relative paths.</summary>
public static class PosixPath
{
    /// <summary><c>posix.dirname</c>: the path up to its last separator, trailing separators ignored.</summary>
    public static string Dirname(string path)
    {
        if (path.Length == 0) return ".";
        var root = path[0] == '/';
        var end = -1;
        var matchedSlash = true;
        for (var i = path.Length - 1; i >= 1; i--)
        {
            if (path[i] == '/')
            {
                if (!matchedSlash)
                {
                    end = i;
                    break;
                }
            }
            else matchedSlash = false;
        }
        if (end == -1) return root ? "/" : ".";
        if (root && end == 1) return "//";
        return path[..end];
    }

    /// <summary><c>posix.join</c>: the non-empty parts joined with <c>/</c>, normalized.</summary>
    public static string Join(params string[] parts)
    {
        var joined = string.Join('/', parts.Where(part => part.Length > 0));
        return joined.Length == 0 ? "." : Normalize(joined);
    }

    /// <summary><c>posix.normalize</c>: <c>.</c> and <c>..</c> resolved, repeated slashes collapsed, a trailing slash kept.</summary>
    public static string Normalize(string path)
    {
        if (path.Length == 0) return ".";
        var absolute = path[0] == '/';
        var trailing = path[^1] == '/';
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0 && segments[^1] != "..") segments.RemoveAt(segments.Count - 1);
                else if (!absolute) segments.Add("..");
                continue;
            }
            segments.Add(segment);
        }
        var result = string.Join('/', segments);
        if (result.Length == 0 && !absolute) result = ".";
        if (result.Length > 0 && trailing) result += "/";
        return absolute ? "/" + result : result;
    }
}
