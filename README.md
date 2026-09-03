# Xenon

**A statically typed, LLVM-backed language for building native applications and libraries.**

[![Tests](https://img.shields.io/github/actions/workflow/status/ZemMezzer/xenon/ci.yml?branch=main&label=tests)](https://github.com/ZemMezzer/xenon/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/ZemMezzer/xenon?display_name=tag&sort=semver&label=release)](https://github.com/ZemMezzer/xenon/releases/latest)
[![Documentation](https://img.shields.io/badge/documentation-xenonlang.com-2563eb)](https://xenonlang.com/)
[![License](https://img.shields.io/badge/license-Apache_2.0-D22128)](LICENSE)

## What is Xenon?

Xenon is a native compiled programming language and toolchain built on LLVM. It combines familiar, strongly typed syntax with direct access to native code and a project system designed for applications, static libraries, and shared libraries.

The repository contains the compiler, LLVM code generator, build driver, project system, command-line interface, and language server. Xenon currently supports:

- native executables and static or shared libraries;
- structs, enums, interfaces, inheritance, properties, and rich built-in operators;
- arrays, pointers, flow-sensitive exclusive/shared borrowing, explicit local/parameter/`this`-field `move`, single-owner `unique<T>`, reference-counted `shared<T>` and observing `weak<T>` (including owned arrays), restartable typed `storage<T>`, address-stable `pin<T>`, partial-move flow analysis, recursive copyability and ownership-aware copy/destructor glue, deterministic scope cleanup, and native `extern` functions;
- multi-project builds through `.xeproj` files and project references;
- debug and release profiles, target triples, LLVM IR emission, and object-file emission;
- editor tooling through the built-in Language Server Protocol implementation.

## Quick start

Download the archive for your platform from the **[latest GitHub release](https://github.com/ZemMezzer/xenon/releases/latest)** and add the extracted `xenon` executable to your `PATH`.

Release archives are Native AOT distributions for Windows x64/Arm64 and Apple Silicon macOS. They do not require a .NET runtime or SDK on the target machine.

Xenon itself still uses LLVM 20 for code generation. Each release archive includes the matching native LLVM runtime (`libLLVM.dll` on Windows or `libLLVM.dylib` on macOS), which must remain next to the `xenon` executable. Xenon also produces native binaries, so a host linker is required:

- **Windows:** Visual Studio 2022 Build Tools with the **Desktop development with C++** workload;
- **macOS:** Xcode Command Line Tools.

Check the installation:

```console
xenon --version
```

Create a directory containing `main.xe`:

```xenon
namespace Hello;

extern int puts(readonly byte* text);

int Main()
{
    puts("Hello, Xenon!");
    return 0;
}
```

Run it directly:

```console
xenon run .
```

Or build a native executable:

```console
xenon build .
xenon build . --release
```

When a directory has no `.xeproj` file, all `.xe` files below it are treated as one implicit executable project.

## Project files

For larger programs, add a `.xeproj` file to define the project explicitly:

```toml
[project]
name = "Hello"
type = "executable"

[source]
root = "src"
```

Place the source code in `src/main.xe`, then build or run the project from its directory:

```console
xenon build --release
xenon run
```

Projects can reference other Xenon projects:

```toml
[references]
projects = ["../Core/Core.xeproj"]
```

See the **[documentation](https://xenonlang.com/)** for the complete project format and language reference.

## Useful CLI commands

```console
# Build the project in the current directory
xenon build

# Build an optimized native binary
xenon build --release

# Build a specific project
xenon build path/to/App.xeproj

# Generate LLVM IR alongside the build output
xenon build --emit-llvm

# Select an LLVM target triple
xenon build --target x86_64-pc-windows-msvc

# Start the language server
xenon lsp
```

Run `xenon --help` to see all available options.

## Building Xenon from source

Building the compiler requires the .NET SDK version selected in [`global.json`](global.json), plus the native toolchain listed in [Quick start](#quick-start). These are build-time requirements only; users of a release archive do not need .NET.

```console
dotnet restore Xenon.sln
dotnet build Xenon.sln --configuration Release
dotnet test Xenon.sln --configuration Release --no-build
```

Build outputs are written to the `out/` directory.

Publish a Native AOT distribution on the matching operating system and architecture:

```console
# Windows x64
dotnet publish src/Xenon.Cli/Xenon.Cli.csproj -c Release -r win-x64

# Apple Silicon macOS
dotnet publish src/Xenon.Cli/Xenon.Cli.csproj -c Release -r osx-arm64
```

The supported release RIDs are `win-x64`, `win-arm64`, and `osx-arm64`. Native AOT distributions are built by GitHub Actions on matching native runners rather than cross-compiled from Linux. Intel macOS (`osx-x64`) is not currently released because the LLVM 20 runtime package used by Xenon does not provide that target.

## License

Copyright 2026 Zem. Xenon is available under the [Apache License 2.0](LICENSE).
