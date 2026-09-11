cmake_minimum_required(VERSION 3.24)

project(XenonSuperbuild LANGUAGES NONE)

include(ExternalProject)

set(XENON_PLATFORM "" CACHE STRING "Xenon target platform: darwin_arm64, win_arm64, or win_x86")
set_property(CACHE XENON_PLATFORM PROPERTY STRINGS darwin_arm64 win_arm64 win_x86)
set(XENON_VERSION "" CACHE STRING "Optional Xenon release version override")

if(NOT XENON_PLATFORM)
  string(TOLOWER "${CMAKE_HOST_SYSTEM_PROCESSOR}" _xenon_host_processor)
  if(APPLE AND _xenon_host_processor MATCHES "^(arm64|aarch64)$")
    set(XENON_PLATFORM darwin_arm64)
  elseif(WIN32 AND _xenon_host_processor MATCHES "^(arm64|aarch64)$")
    set(XENON_PLATFORM win_arm64)
  elseif(WIN32 AND CMAKE_GENERATOR_PLATFORM STREQUAL "Win32")
    set(XENON_PLATFORM win_x86)
  elseif(WIN32)
    set(_xenon_detected_platform win_x64)
  elseif(APPLE)
    set(_xenon_detected_platform darwin_x86_64)
  else()
    set(_xenon_detected_platform "${CMAKE_HOST_SYSTEM_NAME}_${_xenon_host_processor}")
    string(TOLOWER "${_xenon_detected_platform}" _xenon_detected_platform)
  endif()
  if(NOT XENON_PLATFORM)
    message(FATAL_ERROR "Unsupported Xenon build platform: ${_xenon_detected_platform}")
  endif()
endif()

# Keep the complete platform -> RID mapping in one place.
if(XENON_PLATFORM STREQUAL "darwin_arm64")
  set(XENON_DOTNET_RID osx-arm64)
  set(XENON_EXECUTABLE_NAME xenon)
elseif(XENON_PLATFORM STREQUAL "win_arm64")
  set(XENON_DOTNET_RID win-arm64)
  set(XENON_EXECUTABLE_NAME xenon.exe)
elseif(XENON_PLATFORM STREQUAL "win_x86")
  set(XENON_DOTNET_RID win-x86)
  set(XENON_EXECUTABLE_NAME xenon.exe)
else()
  message(FATAL_ERROR "Unsupported Xenon build platform: ${XENON_PLATFORM}")
endif()

include("${CMAKE_SOURCE_DIR}/pipelines/${XENON_PLATFORM}/platform.cmake")

set(XENON_PLATFORM_BUILD_DIRECTORY "${CMAKE_SOURCE_DIR}/build/${XENON_PLATFORM}")
set(XENON_LLVM_SOURCE_DIRECTORY "${CMAKE_SOURCE_DIR}/llvm/llvm-project")
set(XENON_LLVM_BUILD_DIRECTORY "${XENON_PLATFORM_BUILD_DIRECTORY}/llvm")
set(XENON_DOTNET_BUILD_DIRECTORY "${XENON_PLATFORM_BUILD_DIRECTORY}/xenon")
set(XENON_TEST_BUILD_DIRECTORY "${XENON_PLATFORM_BUILD_DIRECTORY}/tests")
set(XENON_PUBLISH_DIRECTORY "${XENON_DOTNET_BUILD_DIRECTORY}/publish")
set(XENON_LLVM_NATIVE_PROPS "${CMAKE_BINARY_DIR}/llvm-native.props")

file(REAL_PATH "${CMAKE_BINARY_DIR}" _xenon_binary_directory)
file(REAL_PATH "${XENON_PLATFORM_BUILD_DIRECTORY}/cmake" _xenon_expected_binary_directory)
if(NOT _xenon_binary_directory STREQUAL _xenon_expected_binary_directory)
  message(FATAL_ERROR
    "CMake artifacts must be placed in build/${XENON_PLATFORM}/cmake. "
    "Configure with: cmake -S . -B build/${XENON_PLATFORM}/cmake -DXENON_PLATFORM=${XENON_PLATFORM}")
endif()

if(NOT EXISTS "${XENON_LLVM_SOURCE_DIRECTORY}/llvm/CMakeLists.txt")
  message(FATAL_ERROR
    "LLVM submodule is missing. Run: git submodule update --init --recursive")
endif()

find_program(DOTNET_EXECUTABLE dotnet REQUIRED)

set(_xenon_llvm_cmake_args
  -DCMAKE_BUILD_TYPE:STRING=Release
  -DCMAKE_POSITION_INDEPENDENT_CODE:BOOL=ON
  -DBUILD_SHARED_LIBS:BOOL=OFF
  -DLLVM_TARGETS_TO_BUILD:STRING=X86|AArch64
  -DLLVM_BUILD_LLVM_DYLIB:BOOL=OFF
  -DLLVM_BUILD_LLVM_C_DYLIB:BOOL=OFF
  -DLLVM_LINK_LLVM_DYLIB:BOOL=OFF
  -DLLVM_BUILD_TOOLS:BOOL=OFF
  -DLLVM_BUILD_UTILS:BOOL=OFF
  -DLLVM_INCLUDE_TESTS:BOOL=OFF
  -DLLVM_INCLUDE_BENCHMARKS:BOOL=OFF
  -DLLVM_INCLUDE_EXAMPLES:BOOL=OFF
  -DLLVM_INCLUDE_DOCS:BOOL=OFF
  -DLLVM_ENABLE_PROJECTS:STRING=
  -DLLVM_ENABLE_RTTI:BOOL=OFF
  -DLLVM_ENABLE_EH:BOOL=OFF
  -DLLVM_ENABLE_ASSERTIONS:BOOL=OFF
  -DLLVM_ENABLE_ZLIB:BOOL=OFF
  -DLLVM_ENABLE_ZSTD:BOOL=OFF
  -DLLVM_ENABLE_LIBXML2:BOOL=OFF
  -DLLVM_ENABLE_CURL:BOOL=OFF
  -DLLVM_ENABLE_LIBEDIT:BOOL=OFF
  -DLLVM_ENABLE_TERMINFO:BOOL=OFF
  -DLLVM_ENABLE_LIBPFM:BOOL=OFF
  ${XENON_LLVM_CMAKE_ARGS})

ExternalProject_Add(llvm-static
  PREFIX "${CMAKE_BINARY_DIR}/llvm-prefix"
  SOURCE_DIR "${XENON_LLVM_SOURCE_DIRECTORY}/llvm"
  BINARY_DIR "${XENON_LLVM_BUILD_DIRECTORY}"
  DOWNLOAD_COMMAND ""
  UPDATE_COMMAND ""
  PATCH_COMMAND ""
  INSTALL_COMMAND ""
  LIST_SEPARATOR |
  CMAKE_ARGS ${_xenon_llvm_cmake_args}
  BUILD_ALWAYS TRUE
  BUILD_COMMAND
    "${CMAKE_COMMAND}" --build <BINARY_DIR> --config Release --target llvm-config
    COMMAND "${CMAKE_COMMAND}" --build <BINARY_DIR> --config Release --target
      LLVMPasses LLVMX86CodeGen LLVMAArch64CodeGen
    COMMAND "${CMAKE_COMMAND}"
      "-DLLVM_BUILD_DIRECTORY=<BINARY_DIR>"
      "-DOUTPUT_FILE=${XENON_LLVM_NATIVE_PROPS}"
      -P "${CMAKE_SOURCE_DIR}/cmake/WriteLlvmNativeProps.cmake"
  BUILD_BYPRODUCTS "${XENON_LLVM_NATIVE_PROPS}"
  USES_TERMINAL_BUILD TRUE)

file(GLOB_RECURSE _xenon_restore_inputs CONFIGURE_DEPENDS
  "${CMAKE_SOURCE_DIR}/xenon/*.csproj"
  "${CMAKE_SOURCE_DIR}/xenon/*.props"
  "${CMAKE_SOURCE_DIR}/xenon/*.targets"
  "${CMAKE_SOURCE_DIR}/xenon/NuGet.config"
  "${CMAKE_SOURCE_DIR}/xenon/global.json")
set(_xenon_restore_stamp "${CMAKE_BINARY_DIR}/xenon-restore.stamp")
add_custom_command(
  OUTPUT "${_xenon_restore_stamp}"
  COMMAND "${DOTNET_EXECUTABLE}" restore "${CMAKE_SOURCE_DIR}/xenon/Xenon.sln"
    --runtime "${XENON_DOTNET_RID}"
    "-p:XenonRootBuildDirectory=${XENON_PLATFORM_BUILD_DIRECTORY}"
  COMMAND "${CMAKE_COMMAND}" -E touch "${_xenon_restore_stamp}"
  DEPENDS ${_xenon_restore_inputs}
  WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}"
  COMMENT "Restoring Xenon .NET dependencies"
  VERBATIM)
add_custom_target(xenon-restore DEPENDS "${_xenon_restore_stamp}")

add_custom_target(xenon-native ALL
  COMMAND "${CMAKE_COMMAND}" -E make_directory "${XENON_PUBLISH_DIRECTORY}"
  COMMAND "${DOTNET_EXECUTABLE}" publish
    "${CMAKE_SOURCE_DIR}/xenon/src/Xenon.Cli/Xenon.Cli.csproj"
    --configuration Release
    --runtime "${XENON_DOTNET_RID}"
    --self-contained true
    --no-restore
    --output "${XENON_PUBLISH_DIRECTORY}"
    "-p:XenonRootBuildDirectory=${XENON_PLATFORM_BUILD_DIRECTORY}"
    -p:XenonStaticLLVM=true
    "-p:LLVMBuildDirectory=${XENON_LLVM_BUILD_DIRECTORY}"
    "-p:LLVMNativePropsFile=${XENON_LLVM_NATIVE_PROPS}"
    $<$<BOOL:${XENON_VERSION}>:-p:XenonVersion=${XENON_VERSION}>
  DEPENDS llvm-static xenon-restore
  BYPRODUCTS "${XENON_PUBLISH_DIRECTORY}/${XENON_EXECUTABLE_NAME}"
  WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}"
  COMMENT "Publishing Xenon NativeAOT (${XENON_DOTNET_RID}) with static LLVM"
  USES_TERMINAL
  VERBATIM)

add_custom_target(xenon-smoke
  COMMAND "${XENON_PUBLISH_DIRECTORY}/${XENON_EXECUTABLE_NAME}" --version
  DEPENDS xenon-native
  COMMENT "Smoke-testing the NativeAOT Xenon executable"
  USES_TERMINAL
  VERBATIM)

set(_xenon_compile_smoke_directory "${XENON_PLATFORM_BUILD_DIRECTORY}/compile-smoke")
add_custom_target(xenon-compile-smoke
  COMMAND "${CMAKE_COMMAND}" -E make_directory "${_xenon_compile_smoke_directory}"
  COMMAND "${CMAKE_COMMAND}" -E copy_if_different
    "${CMAKE_SOURCE_DIR}/xenon/tests/Xenon.EndToEnd.Tests/Cases/Basics/Hello/main.xe"
    "${_xenon_compile_smoke_directory}/main.xe"
  COMMAND "${XENON_PUBLISH_DIRECTORY}/${XENON_EXECUTABLE_NAME}"
    run "${_xenon_compile_smoke_directory}" --profile release
  DEPENDS xenon-native
  COMMENT "Compiling and running a minimal Xenon program with the NativeAOT executable"
  USES_TERMINAL
  VERBATIM)

# Managed testhosts cannot consume static archives, so the two LLVM-using test
# projects carry an isolated private runtime asset. Production restore/publish
# remains on the repository-built static LLVM path above.
add_custom_target(xenon-tests
  COMMAND "${DOTNET_EXECUTABLE}" test "${CMAKE_SOURCE_DIR}/xenon/Xenon.sln"
    --configuration Release
    "-p:XenonRootBuildDirectory=${XENON_TEST_BUILD_DIRECTORY}"
  WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}"
  COMMENT "Running Xenon managed and end-to-end tests"
  USES_TERMINAL
  VERBATIM)

ExternalProject_Add(llvm-static-smoke
  EXCLUDE_FROM_ALL TRUE
  PREFIX "${CMAKE_BINARY_DIR}/llvm-smoke-prefix"
  SOURCE_DIR "${CMAKE_SOURCE_DIR}/cmake/llvm-smoke"
  BINARY_DIR "${XENON_PLATFORM_BUILD_DIRECTORY}/llvm-smoke"
  DOWNLOAD_COMMAND ""
  UPDATE_COMMAND ""
  PATCH_COMMAND ""
  INSTALL_COMMAND ""
  CMAKE_ARGS
    -DCMAKE_BUILD_TYPE:STRING=Release
    "-DLLVM_DIR:PATH=${XENON_LLVM_BUILD_DIRECTORY}/lib/cmake/llvm"
  BUILD_COMMAND "${CMAKE_COMMAND}" --build <BINARY_DIR> --config Release
  DEPENDS llvm-static
  USES_TERMINAL_BUILD TRUE)

add_custom_target(check-source-clean
  COMMAND "${CMAKE_COMMAND}" "-DREPOSITORY_ROOT=${CMAKE_SOURCE_DIR}"
    -P "${CMAKE_SOURCE_DIR}/cmake/CheckSourceClean.cmake"
  COMMENT "Checking that generated artifacts stayed under build/"
  VERBATIM)

add_custom_target(check
  DEPENDS xenon-smoke xenon-compile-smoke llvm-static-smoke check-source-clean)

message(STATUS "Xenon platform: ${XENON_PLATFORM}")
message(STATUS "Xenon .NET RID: ${XENON_DOTNET_RID}")
message(STATUS "LLVM source: ${XENON_LLVM_SOURCE_DIRECTORY}")
message(STATUS "All generated output: ${XENON_PLATFORM_BUILD_DIRECTORY}")
