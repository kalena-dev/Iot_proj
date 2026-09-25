namespace SmartStore.Infrastructure;

/// <summary>
/// Loads simple KEY=VALUE entries into the process environment before the
/// ASP.NET Core configuration pipeline is created.
/// </summary>
public static class DotEnvLoader
{
    public static string? Load()
    {
        var explicitPath = Environment.GetEnvironmentVariable("MONGODB_ENV_FILE");
        var path = !string.IsNullOrWhiteSpace(explicitPath)
            ? Path.GetFullPath(explicitPath)
            : FindInCurrentOrParentDirectories(".env")
                ?? "/home/matt/Downloads/atlas-credentials.env";

        if (path is null || !File.Exists(path))
        {
            return null;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].TrimStart();
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            // Explicit process/system environment variables take precedence.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        return path;
    }

    private static string? FindInCurrentOrParentDirectories(string fileName)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        for (var level = 0; directory is not null && level < 5; level++)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
