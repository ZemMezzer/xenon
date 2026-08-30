using System.Reflection;
using Xenon.Cli;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class BuildVersionTests
{
    [Fact]
    public void CliVersionComesFromAssemblyMetadata()
    {
        string? informationalVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(informationalVersion));
        Assert.Equal(informationalVersion, Program.ProductVersion);
    }
}
