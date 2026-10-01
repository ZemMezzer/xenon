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
- first-class function values, explicit capture lists, ownership-aware closures, and raw function pointers for native callbacks.

## Quick start

Download the archive for your platform from the **[latest GitHub release](https://github.com/ZemMezzer/xenon/releases/latest)** and add the extracted `xenon` executable to your `PATH`.

Release archives are NativeAOT distributions for Windows x64/Arm64 and Apple Silicon macOS. They do not require a .NET runtime or SDK on the target machine.

Xenon uses LLVM 20 for code generation. Release executables contain the required LLVM code through static linkage; no adjacent `LLVM-C.dll`, `libLLVM.dll`, `libLLVM.dylib`, or `libLLVM.so` is required. Xenon also produces native binaries, so a host linker is required:

- **Windows:** Visual Studio 2022 or 2026 Build Tools with the **Desktop development with C++** workload;
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

CMake at the repository root owns the complete build graph: it builds the pinned LLVM submodule as static libraries, restores the .NET solution, and publishes the NativeAOT compiler with those libraries linked into the executable. Required host dependencies are:

- Git with submodule support;
- CMake 3.24 or newer (4.2 or newer for Visual Studio 2026);
- the .NET SDK selected by [`xenon/global.json`](xenon/global.json);
- Visual Studio 2022 or 2026 Build Tools with the Desktop development with C++ workload and a Windows SDK on Windows;
- Xcode Command Line Tools on Apple Silicon macOS.

Clone and build on an AMD64 Windows host (the default is `win_x64`):

```console
git clone --recurse-submodules https://github.com/ZemMezzer/xenon.git
cd xenon
Build.bat
```

`Build.bat win_x64` selects the same platform explicitly; use `Build.bat win_arm64` for Windows Arm64. The script uses the installed Visual Studio generator selected by CMake; set `CMAKE_GENERATOR` to select a specific version. CI pins Windows x64 to VS 2022 and Windows Arm64 to the `windows-11-vs2026-arm` image with VS 2026.

For a direct Visual Studio CMake configure, select both the Xenon platform and generator architecture:

```console
cmake -S . -B build/win_x64/cmake -G "Visual Studio 17 2022" -A x64 -DXENON_PLATFORM=win_x64
cmake --build build/win_x64/cmake --config Release --target xenon-native
```

On Apple Silicon macOS:

```console
git clone --recurse-submodules https://github.com/ZemMezzer/xenon.git
cd xenon
./Build.sh
```

For an existing clone, initialize LLVM with `git submodule update --init --recursive`. The platform matrix is:

| Xenon platform | .NET RID | Binary architecture | Visual Studio `-A` |
| --- | --- | --- | --- |
| `win_x64` | `win-x64` | x86-64 | `x64` |
| `win_arm64` | `win-arm64` | Arm64 | `ARM64` |
| `darwin_arm64` | `osx-arm64` | Arm64 | — |

### C ABI support

For C-compatible `extern`, `export`, and raw C function pointer signatures:

| Target | Scalar / pointer C ABI | Struct parameters by value | Struct returns by value |
| --- | --- | --- | --- |
| `win_x64` | Supported | Supported | Supported |
| `win_arm64` | Supported | Supported | Supported |
| `darwin_arm64` | Supported | Supported | Supported |

Windows x86 (32-bit) is not a supported build or compiler target; use `win_x64` or `win_arm64` instead.

The superbuild keeps LLVM as a static dependency with X86 and AArch64 code-generation backends, then publishes Xenon through NativeAOT. All generated CMake, LLVM, MSBuild, and NativeAOT files live under `build/<platform>/`: CMake under `cmake/`, LLVM under `llvm/`, and the final executable under `xenon/publish/`. Deleting root `build/` performs a complete clean. The `check` target runs the statically linked LLVM C++ smoke test, `xenon --version`, compiles and runs a minimal Xenon program, checks build-system mappings, and verifies source-tree cleanliness:

```console
cmake --build build/win_x64/cmake --config Release --target check
```

You can run the platform and XML checks without building LLVM:

```console
cmake -P cmake/tests/BuildSystemChecks.cmake
```

Run the complete managed and end-to-end suite through CMake with `--target xenon-tests`. The .NET solution remains available at `xenon/Xenon.sln`; direct restore/build/test commands also redirect outputs to `build/local/`, so they do not create `bin/` or `obj/` directories in the source tree.

## Compiler internals

Executable bodies pass through verified MIR before LLVM code generation. MIR owns
control flow, ownership and lifetime analysis, cleanup, exception edges and
coroutine transformation. See the [architecture audit](docs/mir-architecture.md),
[MIR dump commands](docs/mir-tooling.md) and [migration checkpoints](docs/mir-migration.md).
## License

Copyright 2026 Zem. Xenon is available under the [Apache License 2.0](LICENSE).
