using System.IO;
using HeyRed.Mime;

namespace Wingman;

internal record MimeInfo(string MimeType, string CharSet)
{
    internal bool IsText => MimeType.StartsWith("text/", StringComparison.Ordinal) || TextMimeTypes.Contains(MimeType);

    // non-text/* mime types that are still human-readable text
    private static readonly HashSet<string> TextMimeTypes =
    [
        "inode/x-empty",
        "application/json",
        "application/xml",
        "application/xhtml+xml",
        "application/javascript",
        "application/x-javascript",
        "application/ecmascript",
        "application/typescript",
        "application/x-sh",
        "application/x-shellscript",
        "application/x-csh",
        "application/x-bash",
        "application/x-awk",
        "application/x-perl",
        "application/x-ruby",
        "application/x-python",
        "application/x-httpd-php",
        "application/x-msdos-batch",
        "application/x-powershell",
        "application/sql",
        "application/toml",
        "application/yaml",
        "application/x-yaml",
        "application/x-empty",
    ];
}

internal static class MimeDetector
{
    private const MagicOpenFlags SkipFlags =
        MagicOpenFlags.MAGIC_ERROR |
        MagicOpenFlags.MAGIC_NO_CHECK_COMPRESS |
        MagicOpenFlags.MAGIC_NO_CHECK_ELF |
        MagicOpenFlags.MAGIC_NO_CHECK_APPTYPE;

    public static MimeInfo Detect(string filePath)
    {
        // empty files have no meaningful encoding
        if (new FileInfo(filePath).Length == 0)
            return new MimeInfo("inode/x-empty", "utf-8");

        string mimeType;
        string charSet;

        try
        {
            using var typeMagic = new Magic(MagicOpenFlags.MAGIC_MIME_TYPE | SkipFlags);
            mimeType = typeMagic.Read(filePath);
            if (!IsValidMimeType(mimeType))
                mimeType = MimeGuesser.GuessMimeType(filePath);
        }
        catch
        {
            mimeType = MimeGuesser.GuessMimeType(filePath);
        }

        try
        {
            using var encodingMagic = new Magic(MagicOpenFlags.MAGIC_MIME_ENCODING | SkipFlags);
            charSet = encodingMagic.Read(filePath);
            if (!IsValidCharSet(charSet))
                charSet = "utf-8";
        }
        catch
        {
            charSet = "utf-8";
        }

        return new MimeInfo(mimeType, string.IsNullOrEmpty(charSet) ? "utf-8" : charSet);
    }

    // libmagic returns descriptive text (e.g. "writable, writable, no read permission") for unreadable files
    // instead of throwing — validate the result looks like a real mime type or charset token
    private static bool IsValidMimeType(string value) =>
        value.Contains('/') && !value.Contains(' ') && !value.Contains(',');

    private static bool IsValidCharSet(string value) =>
        !string.IsNullOrEmpty(value) && !value.Contains(' ') && !value.Contains(',');
}
