using Xenon.Driver;
using Xunit;

namespace Xenon.Compiler.Tests.Driver;

public sealed class NativeExceptionRuntimeTests
{
    [Fact]
    public async Task CaughtNestedRethrownAndConcurrentExceptionsPreserveHostTerminateHandler()
    {
        string directory = Path.Combine(Path.GetTempPath(), "xenon-eh-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "runtime.cpp"), NativeExceptionRuntime.Source);
            File.WriteAllText(Path.Combine(directory, "CMakeLists.txt"), """
                cmake_minimum_required(VERSION 3.24)
                project(XenonEhProbe LANGUAGES CXX)
                set(CMAKE_CXX_STANDARD 17)
                find_package(Threads REQUIRED)
                add_executable(probe runtime.cpp probe.cpp)
                target_link_libraries(probe PRIVATE Threads::Threads)
                if(MSVC)
                  target_compile_options(probe PRIVATE /EHa)
                endif()
                """);
            File.WriteAllText(Path.Combine(directory, "probe.cpp"), """
                #include <atomic>
                #include <cstddef>
                #include <cstdint>
                #include <cstdlib>
                #include <cstring>
                #include <cstdio>
                #include <exception>
                #include <thread>
                #include <chrono>
                #include <vector>

                extern "C" void* __xenon_eh_allocate(std::uintptr_t, std::uintptr_t,
                    const char*, const char*, void (*)(void*));
                extern "C" [[noreturn]] void __xenon_eh_throw(void*);
                extern "C" void* __xenon_eh_current();
                extern "C" void __xenon_eh_handle(void*);
                extern "C" [[noreturn]] void __xenon_eh_rethrow();

                // Native ABI tests intentionally bypass Xenon source identifiers.
                extern "C" void* __xenon_async_root_create();
                extern "C" void __xenon_async_root_retain(void*);
                extern "C" void __xenon_async_root_release(void*);
                extern "C" void __xenon_async_root_notify(void*);
                extern "C" void __xenon_async_root_pump(void*);
                extern "C" void __xenon_async_root_close(void*);

                void check_async_root() {
                    void* root = __xenon_async_root_create();
                    __xenon_async_root_retain(root);
                    // Notifications before wait must not be lost; duplicates coalesce.
                    __xenon_async_root_notify(root);
                    __xenon_async_root_notify(root);
                    __xenon_async_root_pump(root);
                    std::thread worker([=] {
                        std::this_thread::sleep_for(std::chrono::milliseconds(10));
                        __xenon_async_root_notify(root);
                    });
                    __xenon_async_root_pump(root);
                    worker.join();
                    // A retained continuation can safely arrive after entry completion.
                    __xenon_async_root_close(root);
                    __xenon_async_root_notify(root);
                    __xenon_async_root_pump(root);
                    __xenon_async_root_release(root);
                }

                [[noreturn]] void host_handler() {
                    std::fputs("host terminate called\n", stderr);
                    std::fflush(stderr);
                    std::_Exit(73);
                }
                [[noreturn]] void raise_xenon() {
                    __xenon_eh_throw(__xenon_eh_allocate(sizeof(int), alignof(int),
                        "int", "int", nullptr));
                }

                bool caught_once(std::terminate_handler expected = host_handler) {
                    try { raise_xenon(); }
                    catch (...) { __xenon_eh_handle(__xenon_eh_current()); }
                    return std::get_terminate() == expected;
                }

                bool nested_and_rethrow() {
                    try { raise_xenon(); }
                    catch (...) {
                        void* outer = __xenon_eh_current();
                        try { raise_xenon(); }
                        catch (...) { __xenon_eh_handle(__xenon_eh_current()); }
                        if (__xenon_eh_current() != outer) return false;
                        try { __xenon_eh_rethrow(); }
                        catch (...) {
                            if (__xenon_eh_current() != outer) return false;
                            __xenon_eh_handle(outer);
                        }
                    }
                    return std::get_terminate() == host_handler;
                }

                int main(int argc, char** argv) {
                    std::set_terminate(host_handler);
                    if (argc > 1 && std::strcmp(argv[1], "uncaught") == 0) raise_xenon();
                    if (argc > 1 && std::strcmp(argv[1], "single") == 0)
                        return caught_once() ? 42 : 1;
                    if (argc > 1 && std::strcmp(argv[1], "nested") == 0)
                        return nested_and_rethrow() ? 42 : 1;
                    check_async_root();
                    if (!caught_once() || !nested_and_rethrow()) return 1;
                    std::atomic<bool> ok{true};
                    std::vector<std::thread> threads;
                    for (int i = 0; i < 4; ++i)
                        threads.emplace_back([&] {
                            auto previous = std::get_terminate();
                            for (int j = 0; j < 20; ++j)
                                if (!caught_once(previous)) ok = false;
                        });
                    for (auto& thread : threads) thread.join();
                    return ok && std::get_terminate() == host_handler ? 42 : 2;
                }
                """);

            string build = Path.Combine(directory, "build");
            await RunTool("cmake", ["-S", directory, "-B", build], directory);
            await RunTool("cmake", ["--build", build, "--config", "Release", "--target", "probe"],
                directory);
            string executable = OperatingSystem.IsWindows()
                ? Path.Combine(build, "Release", "probe.exe")
                : Path.Combine(build, "probe");
            await RunTool(executable, ["single"], directory, expectedExit: 42);
            await RunTool(executable, ["nested"], directory, expectedExit: 42);
            NativeProcessResult caught = await RunTool(executable, [], directory, expectedExit: 42);
            Assert.Equal(42, caught.ExitCode);
            NativeProcessResult uncaught = await RunTool(executable, ["uncaught"], directory,
                expectedExit: 73);
            Assert.Equal(73, uncaught.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<NativeProcessResult> RunTool(
        string executable, IReadOnlyList<string> arguments, string directory, int expectedExit = 0)
    {
        NativeProcessResult result = await new NativeProcessRunner().RunAsync(
            new NativeProcessRequest(executable, arguments, directory, TimeSpan.FromMinutes(3)));
        Assert.True(result.StartError is null && !result.TimedOut && result.TerminationError is null &&
            result.ExitCode == expectedExit,
            $"Process {executable} failed: start={result.StartError}, timeout={result.TimedOut}, " +
            $"termination={result.TerminationError}, exit={result.ExitCode}\n" +
            $"stdout: {result.Stdout}\nstderr: {result.Stderr}");
        return result;
    }
}
