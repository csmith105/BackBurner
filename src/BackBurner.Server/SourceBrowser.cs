using BackBurner.Contracts;
using Microsoft.Extensions.Options;

namespace BackBurner.Server;

public sealed class SourceBrowser
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".avi", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".mts", ".ts", ".vob", ".webm", ".wmv"
    };

    private readonly IReadOnlyDictionary<string, string> roots;
    private readonly int maximumScanFiles;

    public SourceBrowser(IOptions<CoordinatorOptions> options)
    {
        roots = options.Value.SourceRoots.ToDictionary(
            pair => pair.Key,
            pair => Path.GetFullPath(Environment.ExpandEnvironmentVariables(pair.Value)),
            StringComparer.OrdinalIgnoreCase);
        maximumScanFiles = Math.Clamp(options.Value.MaximumScanFiles, 1, 25_000);
    }

    public DirectoryScanResult Scan(DirectoryScanRequest request)
    {
        var (rootName, relativeDirectory, normalizedLogicalDirectory) = ParseLogicalDirectory(request.DirectoryPath);
        if (!roots.TryGetValue(rootName, out var configuredRoot))
        {
            throw new ArgumentException($"Source root '{rootName}' is not available to the coordinator.");
        }

        var root = Path.GetFullPath(configuredRoot);
        var directory = Path.GetFullPath(Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithinRoot(root, directory))
        {
            throw new ArgumentException("Source directory escapes its configured root.");
        }
        if (!Directory.Exists(directory))
        {
            throw new ArgumentException($"Source directory '{normalizedLogicalDirectory}' does not exist.");
        }

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = request.Recursive,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 32,
            MatchCasing = MatchCasing.CaseInsensitive
        };
        var files = new List<ScannedMediaFile>();
        var truncated = false;
        foreach (var physicalFile in Directory.EnumerateFiles(directory, "*", enumeration))
        {
            if (!MediaExtensions.Contains(Path.GetExtension(physicalFile)))
            {
                continue;
            }
            if (files.Count == maximumScanFiles)
            {
                truncated = true;
                break;
            }

            var fullFile = Path.GetFullPath(physicalFile);
            if (!IsWithinRoot(root, fullFile))
            {
                throw new IOException("A scanned file escaped its configured source root.");
            }
            var relativeToRoot = Path.GetRelativePath(root, fullFile).Replace('\\', '/');
            var relativeToDirectory = Path.GetRelativePath(directory, fullFile).Replace('\\', '/');
            files.Add(new ScannedMediaFile(
                $"{rootName}:/{relativeToRoot}",
                relativeToDirectory,
                Path.GetFileName(fullFile),
                new FileInfo(fullFile).Length));
        }

        files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return new DirectoryScanResult(normalizedLogicalDirectory, files, truncated);
    }

    private static (string Root, string Relative, string Normalized) ParseLogicalDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Source directory is required.");
        }
        var normalized = value.Trim().Replace('\\', '/');
        var separator = normalized.IndexOf(":/", StringComparison.Ordinal);
        if (separator < 1 || separator > 50)
        {
            throw new ArgumentException("Use a logical directory such as nas-media:/Show/Season 01.");
        }
        var root = normalized[..separator];
        if (root.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new ArgumentException($"Logical root '{root}' contains unsupported characters.");
        }
        var parts = normalized[(separator + 2)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".."))
        {
            throw new ArgumentException("Source directory may not contain traversal segments.");
        }
        var relative = string.Join('/', parts);
        var logical = relative.Length == 0 ? $"{root}:/" : $"{root}:/{relative}";
        return (root, relative, logical);
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return string.Equals(root, candidate, comparison) || candidate.StartsWith(rootWithSeparator, comparison);
    }
}
