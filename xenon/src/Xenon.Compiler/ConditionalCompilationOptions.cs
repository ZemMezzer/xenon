using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler;

/// <summary>A stable, case-sensitive conditional environment for one source snapshot.</summary>
public sealed class ConditionalCompilationOptions : IEquatable<ConditionalCompilationOptions>
{
    public static ConditionalCompilationOptions Default { get; } = new();

    public ConditionalCompilationOptions(IEnumerable<string>? defines = null, string? targetTriple = null,
        string profile = "debug", IEnumerable<string>? projectDefines = null, bool allowProfileDefines = true)
    {
        if (profile is not ("debug" or "release"))
            throw new ArgumentException($"unknown build profile '{profile}'", nameof(profile));
        TargetTriple = targetTriple is null ? null : CompilationTarget.Normalize(targetTriple);
        AllowProfileDefines = allowProfileDefines;
        Profile = profile;
        static ImmutableSortedSet<string> Capture(IEnumerable<string>? values)
        {
            var result = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (string name in values ?? [])
            {
                ValidateUserDefine(name);
                result.Add(name);
            }
            return result.ToImmutable();
        }
        GlobalDefines = Capture(defines);
        ProjectDefines = Capture(projectDefines);
        UserDefines = GlobalDefines.Union(ProjectDefines);
        Defines = UserDefines.Union(CompilationTarget.GetDefines(TargetTriple));
        if (allowProfileDefines) Defines = Defines.Add(profile == "debug" ? "XENON_DEBUG" : "XENON_RELEASE");
        Identity = $"{TargetTriple}|{(allowProfileDefines ? profile : "library")}|global:{string.Join(",", GlobalDefines)}|local:{string.Join(",", ProjectDefines)}";
    }

    public string? TargetTriple { get; }
    public string Profile { get; }
    public bool AllowProfileDefines { get; }
    public ImmutableSortedSet<string> GlobalDefines { get; }
    public ImmutableSortedSet<string> ProjectDefines { get; }
    public ImmutableSortedSet<string> UserDefines { get; }
    public ImmutableSortedSet<string> Defines { get; }
    public string Identity { get; }
    public ConditionalCompilationOptions WithTarget(string triple) => new(GlobalDefines, triple, Profile, ProjectDefines, AllowProfileDefines);
    public ConditionalCompilationOptions ForLibraryBuild() => new(GlobalDefines, TargetTriple ?? CompilationTarget.DefaultTriple, "debug", ProjectDefines, allowProfileDefines: false);
    public bool Equals(ConditionalCompilationOptions? other) => other?.Identity == Identity;
    public override bool Equals(object? obj) => obj is ConditionalCompilationOptions other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Identity);

    public static bool IsIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name) || !IdentifierFacts.TryGetStart(name, 0, out int width))
            return false;
        for (int index = width; index < name.Length; index += width)
            if (!IdentifierFacts.TryGetContinue(name, index, out width))
                return false;
        return true;
    }

    public static void ValidateUserDefine(string name)
    {
        if (!IsIdentifier(name))
            throw new ArgumentException($"invalid conditional define '{name}': expected an identifier, without a value");
        if (name.StartsWith("XENON_", StringComparison.Ordinal))
            throw new ArgumentException($"conditional define '{name}' uses the compiler-reserved XENON_ prefix");
    }
}

/// <summary>Canonical target facts for compilation target selection and predefined symbols.</summary>
public static class CompilationTarget
{
    public static string Normalize(string triple)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triple);
        string value = triple.Trim().ToLowerInvariant();
        value = value switch
        {
            "windows-x64" => "x86_64-pc-windows-msvc",
            "windows-arm64" => "aarch64-pc-windows-msvc",
            "windows-x86" => "i686-pc-windows-msvc",
            "linux-x64" => "x86_64-unknown-linux-gnu",
            "linux-arm64" => "aarch64-unknown-linux-gnu",
            "macos-x64" => "x86_64-apple-darwin",
            "macos-arm64" => "aarch64-apple-darwin",
            _ => value,
        };
        string[] parts = value.Split('-');
        if (parts.Length < 3 || parts.Any(part => part.Length == 0 || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_'))))
            throw new ArgumentException($"invalid target triple '{triple}'");
        parts[0] = parts[0] switch { "amd64" => "x86_64", "arm64" => "aarch64", _ => parts[0] };
        string normalized = string.Join('-', parts);
        if (GetOperatingSystem(normalized) == 1 && GetArchitecture(normalized) == 3)
            throw new ArgumentException(
                $"Windows x86 (32-bit) target '{triple}' is not supported; use windows-x64 or windows-arm64.");
        return normalized;
    }

    // Used only when no target was selected. Explicit targets never consult host facts.
    public static string DefaultTriple
    {
        get
        {
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x86_64",
                Architecture.Arm64 => "aarch64",
                Architecture.X86 => "i686",
                Architecture.Arm => "arm",
                _ => "unknown",
            };
            return OperatingSystem.IsWindows() ? $"{arch}-pc-windows-msvc" :
                OperatingSystem.IsMacOS() ? $"{arch}-apple-darwin" :
                OperatingSystem.IsLinux() ? $"{arch}-unknown-linux-gnu" : $"{arch}-unknown-unknown";
        }
    }

    public static int GetOperatingSystem(string? triple)
    {
        string[] parts = (triple ?? "").ToLowerInvariant().Split('-');
        if (parts.Contains("windows") || parts.Contains("win32")) return 1;
        if (parts.Contains("linux")) return 2;
        if (parts.Any(p => p.StartsWith("darwin", StringComparison.Ordinal) || p.StartsWith("macos", StringComparison.Ordinal))) return 3;
        return 0;
    }

    public static int GetArchitecture(string? triple) => (triple ?? "").ToLowerInvariant().Split('-')[0] switch
    {
        "x86_64" or "amd64" => 1,
        "aarch64" or "arm64" => 2,
        "i386" or "i486" or "i586" or "i686" or "x86" => 3,
        _ => 0,
    };

    public static IEnumerable<string> GetDefines(string? triple)
    {
        switch (GetOperatingSystem(triple))
        {
            case 1:
                yield return "XENON_WINDOWS";
                break;
            case 2:
                yield return "XENON_LINUX";
                yield return "XENON_UNIX";
                break;
            case 3:
                yield return "XENON_MACOS";
                yield return "XENON_UNIX";
                break;
        }
        switch (GetArchitecture(triple))
        {
            case 1:
                yield return "XENON_X64";
                break;
            case 2:
                yield return "XENON_ARM64";
                break;
            case 3:
                yield return "XENON_X86";
                break;
        }
    }

    public static bool IsTargetDefine(string name) => name is
        "XENON_WINDOWS" or "XENON_LINUX" or "XENON_MACOS" or "XENON_UNIX" or
        "XENON_X64" or "XENON_ARM64" or "XENON_X86";
}
