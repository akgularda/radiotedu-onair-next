namespace OnAirNext.Engine;

public static class LocalMediaPath
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 500 || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Choose a local, absolute music folder or file path.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new ArgumentException("Choose a local drive for the live music folder.");
        return full;
    }

}
