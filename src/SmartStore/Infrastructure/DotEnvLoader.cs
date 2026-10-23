namespace SmartStore.Infrastructure;

/// <summary>Loads private configuration without depending on a machine/user path.</summary>
public static class DotEnvLoader
{
    public static string? Load()
    {
        var explicitPath = Environment.GetEnvironmentVariable("MONGODB_ENV_FILE");
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        // Match the TypeScript service: process > explicit file > project > user config.
        var paths = new[]
        {
            string.IsNullOrWhiteSpace(explicitPath) ? null : Path.GetFullPath(explicitPath),
            FindInCurrentOrParentDirectories(".env"),
            Path.Combine(configHome, "northstar", "credentials.env")
        };
        string? firstLoaded = null;
        foreach (var path in paths.Distinct())
        {
            if (path is null || !File.Exists(path)) continue;
            firstLoaded ??= path;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                {
                    line = line[7..].TrimStart();
                }
                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0) continue;
                var key = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim();
                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[^1] == '"') ||
                     (value[0] == '\'' && value[^1] == '\'')))
                {
                    value = value[1..^1];
                }
                if (Environment.GetEnvironmentVariable(key) is null)
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
        }
        return firstLoaded;
    }

    private static string? FindInCurrentOrParentDirectories(string fileName)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var level = 0; directory is not null && level < 5; level++)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }
}
