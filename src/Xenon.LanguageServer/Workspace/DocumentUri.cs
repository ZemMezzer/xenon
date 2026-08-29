namespace Xenon.LanguageServer;

public static class DocumentUri
{
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string ToNormalizedPath(string uriText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriText);
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri) || !uri.IsFile)
            throw new ArgumentException($"Document URI '{uriText}' is not an absolute file URI.", nameof(uriText));
        return NormalizePath(uri.LocalPath);
    }

    public static Uri FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Uri(NormalizePath(path), UriKind.Absolute);
    }

    public static string NormalizePath(string path) => Path.GetFullPath(path);
}
