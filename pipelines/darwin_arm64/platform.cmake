if(NOT APPLE)
  message(FATAL_ERROR "darwin_arm64 must be configured on macOS")
endif()

set(XENON_LLVM_CMAKE_ARGS
  -DCMAKE_OSX_ARCHITECTURES:STRING=arm64)
