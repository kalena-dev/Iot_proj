using SmartStore.Infrastructure;

namespace SmartStore.Tests;

[CollectionDefinition("Process configuration", DisableParallelization = true)]
public sealed class ProcessConfigurationCollection { }

[Collection("Process configuration")]
public sealed class DotEnvLoaderTests
{
    [Fact]
    public void LoadsPortableFilesWithConsistentPrecedence()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var originalDirectory = Directory.GetCurrentDirectory();
        string[] keys = ["MONGODB_ENV_FILE", "XDG_CONFIG_HOME", "NORTHSTAR_TEST_VALUE",
            "NORTHSTAR_TEST_PROJECT", "NORTHSTAR_TEST_FALLBACK"];
        var original = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "config", "northstar"));
            Directory.SetCurrentDirectory(directory);
            var explicitFile = Path.Combine(directory, "private.env");
            File.WriteAllText(explicitFile, "NORTHSTAR_TEST_VALUE=explicit\n");
            File.WriteAllText(".env", "NORTHSTAR_TEST_VALUE=project\nNORTHSTAR_TEST_PROJECT=loaded\n");
            File.WriteAllText(Path.Combine(directory, "config", "northstar", "credentials.env"),
                "NORTHSTAR_TEST_VALUE=fallback\nNORTHSTAR_TEST_FALLBACK=loaded\n");
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable("MONGODB_ENV_FILE", explicitFile);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(directory, "config"));

            Assert.Equal(explicitFile, DotEnvLoader.Load());
            Assert.Equal("explicit", Environment.GetEnvironmentVariable("NORTHSTAR_TEST_VALUE"));
            Assert.Equal("loaded", Environment.GetEnvironmentVariable("NORTHSTAR_TEST_PROJECT"));
            Assert.Equal("loaded", Environment.GetEnvironmentVariable("NORTHSTAR_TEST_FALLBACK"));

            Environment.SetEnvironmentVariable("NORTHSTAR_TEST_VALUE", "process");
            DotEnvLoader.Load();
            Assert.Equal("process", Environment.GetEnvironmentVariable("NORTHSTAR_TEST_VALUE"));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            foreach (var (key, value) in original) Environment.SetEnvironmentVariable(key, value);
            Directory.Delete(directory, recursive: true);
        }
    }
}
