if(NOT WIN32)
  message(FATAL_ERROR "win_x86 must be configured on Windows")
endif()
if(NOT CMAKE_GENERATOR_PLATFORM STREQUAL "Win32")
  message(FATAL_ERROR "win_x86 requires a CMake generator configured with -A Win32")
endif()

set(XENON_LLVM_CMAKE_ARGS
  -DCMAKE_MSVC_RUNTIME_LIBRARY:STRING=MultiThreaded)
