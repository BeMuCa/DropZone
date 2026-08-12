namespace Dropzone.Mtp;

public enum MediaKind
{
    Other,
    Photo,
    Video
}

public static class MediaClassifier
{
    static readonly HashSet<string> PhotoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".heic", ".heif", ".png", ".gif", ".tif", ".tiff", ".dng", ".webp", ".bmp" };

    static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".mov", ".mp4", ".m4v", ".avi", ".hevc", ".mkv" };

    public static MediaKind Classify(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return MediaKind.Other;
        if (PhotoExtensions.Contains(ext)) return MediaKind.Photo;
        if (VideoExtensions.Contains(ext)) return MediaKind.Video;
        return MediaKind.Other;
    }

    public static bool IsMedia(string fileName) => Classify(fileName) != MediaKind.Other;
}
