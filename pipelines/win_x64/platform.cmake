if(NOT WIN32)
  message(FATAL_ERROR "win_x64 must be configured on Windows")
endif()
if(NOT CMAKE_GENERATOR_PLATFORM STREQUAL "x64" AND
   NOT CMAKE_VS_PLATFORM_NAME STREQUAL "x64")
  message(FATAL_ERROR "win_x64 requires a CMake generator configured with -A x64")
endif()

set(XENON_LLVM_CMAKE_ARGS
  -DCMAKE_MSVC_RUNTIME_LIBRARY:STRING=MultiThreaded)
