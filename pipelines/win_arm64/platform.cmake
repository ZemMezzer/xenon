if(NOT WIN32)
  message(FATAL_ERROR "win_arm64 must be configured on Windows")
endif()
if(NOT CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64")
  message(FATAL_ERROR "win_arm64 requires a CMake generator configured with -A ARM64")
endif()

set(XENON_LLVM_CMAKE_ARGS
  -DCMAKE_MSVC_RUNTIME_LIBRARY:STRING=MultiThreaded)
