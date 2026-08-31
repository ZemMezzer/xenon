using Xenon.EndToEnd.Tests.Infrastructure;
using Xunit;

namespace Xenon.EndToEnd.Tests;

public static class FixtureDiscovery
{
    public static IEnumerable<object[]> Cases(string profile)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Cases");
        foreach (string manifestPath in Directory.EnumerateFiles(root, "test.json", SearchOption.AllDirectories).Order())
        {
            string directory = Path.GetDirectoryName(manifestPath)!;
            // Invalid manifests must produce a normal harness failure, not disappear during discovery.
            TestManifest? manifest = null;
            try { manifest = TestManifest.Load(directory); }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or InvalidDataException) { }
            if (manifest is null || manifest.Profiles.Contains(profile))
                yield return [Path.GetRelativePath(root, directory)];
        }
    }

    public static async Task Run(string fixture, string profile)
    {
        var result = await new E2eHarness().RunAsync(Path.Combine(AppContext.BaseDirectory, "Cases", fixture), profile);
        E2eHarness.AssertSuccess(result);
    }
}

// Separate classes preserve profile filtering and reporting. Assembly-level serialization keeps
// native LLVM use deterministic; the isolation self-test still exercises parallel builds explicitly.
[Trait("Category", "E2E")]
[Trait("Profile", "debug")]
public sealed class DebugRegressionTests
{
    public static IEnumerable<object[]> Cases() => FixtureDiscovery.Cases("debug");
    [Theory, MemberData(nameof(Cases))]
    public Task Fixture(string fixture) => FixtureDiscovery.Run(fixture, "debug");
}

[Trait("Category", "E2E")]
[Trait("Profile", "release")]
public sealed class ReleaseRegressionTests
{
    public static IEnumerable<object[]> Cases() => FixtureDiscovery.Cases("release");
    [Theory, MemberData(nameof(Cases))]
    public Task Fixture(string fixture) => FixtureDiscovery.Run(fixture, "release");
}
