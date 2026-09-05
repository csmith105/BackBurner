namespace BackBurner.Worker.Core;

public sealed class LogicalPathResolver
{
    private readonly IReadOnlyDictionary<string, string> roots;

    public LogicalPathResolver(IReadOnlyDictionary<string, string> roots)
    {
        this.roots = roots.ToDictionary(
            item => item.Key,
            item => Path.GetFullPath(Environment.ExpandEnvironmentVariables(item.Value)),
            StringComparer.OrdinalIgnoreCase);
    }

    public string Resolve(string logicalPath)
    {
        var separator = logicalPath.IndexOf(":/", StringComparison.Ordinal);
        if (separator < 1)
        {
            throw new InvalidOperationException($"'{logicalPath}' is not a logical path.");
        }
        var rootName = logicalPath[..separator];
        if (!roots.TryGetValue(rootName, out var root))
        {
            throw new InvalidOperationException($"Worker has no mapping for logical root '{rootName}'.");
        }

        var relativeParts = logicalPath[(separator + 2)..]
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (relativeParts.Length == 0 || relativeParts.Any(part => part is "." or ".." || Path.IsPathRooted(part)))
        {
            throw new InvalidOperationException($"Logical path '{logicalPath}' is empty or attempts traversal.");
        }

        var candidate = Path.GetFullPath(Path.Combine([root, .. relativeParts]));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidOperationException($"Logical path '{logicalPath}' resolves outside '{rootName}'.");
        }
        return candidate;
    }
}
