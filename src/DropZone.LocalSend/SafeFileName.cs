namespace DropZone.LocalSend;

/// <summary>
/// Filenames arrive from a remote peer, so they are untrusted input. Everything that could
/// steer a write outside the download folder is stripped before the name touches the disk.
/// </summary>
public static class SafeFileName
{
    public const string Fallback = "unnamed";

    public static string Of(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Fallback;

        // Take only the last segment, regardless of which separator the sender used.
        var lastSlash = fileName.LastIndexOfAny(['/', '\\']);
        var name = lastSlash >= 0 ? fileName[(lastSlash + 1)..] : fileName;

        var cleaned = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();

        // "." and ".." survive the filter above but are not usable names.
        if (cleaned.Length == 0 || cleaned.All(c => c == '.'))
            return Fallback;

        return cleaned;
    }

    /// <summary>Resolves a remote filename to an absolute path that is guaranteed to stay inside <paramref name="folder"/>.</summary>
    public static string ResolveInside(string folder, string? fileName)
    {
        var full = Path.GetFullPath(Path.Combine(folder, Of(fileName)));
        var root = Path.GetFullPath(folder);

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to write outside '{root}'.");

        return full;
    }

    /// <summary>Appends " (2)", " (3)" … when the target already exists, so imports never overwrite.</summary>
    public static string Deduplicate(string fullPath)
    {
        if (!File.Exists(fullPath)) return fullPath;

        var dir = Path.GetDirectoryName(fullPath)!;
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);

        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
