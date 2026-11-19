using System.Linq;
using System.Reflection;
using Xunit;

namespace OpenSlideSharp.Tests;

/// <summary>
/// The informational version of the assemblies (Directory.Build.props): the package version, and with
/// OpenSlideUpstreamVersion set (as the release build does) <c>&lt;version&gt;+openslide.&lt;upstream version&gt;</c>.
/// </summary>
public class AssemblyVersionTests
{
    // The properties this test assembly, and the core assembly with it, were built with (see the project file).
    private static string BuildProperty(string name) =>
        typeof(AssemblyVersionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "Build." + name).Value ?? string.Empty;

    [Fact]
    public void InformationalVersion_IsTheVersion_WithTheUpstreamVersionWhenGiven()
    {
        string? informational = typeof(OpenSlideLibrary).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = BuildProperty("Version");
        string upstream = BuildProperty("OpenSlideUpstreamVersion");

        Assert.False(string.IsNullOrEmpty(informational));
        Assert.False(string.IsNullOrEmpty(version));
        Assert.Equal(version, informational!.Split('+')[0]);
        if (upstream.Length > 0)
        {
            // Not followed by the SDK's git commit hash.
            Assert.Equal($"{version}+openslide.{upstream}", informational);
        }
    }
}
