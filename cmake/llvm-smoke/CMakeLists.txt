cmake_minimum_required(VERSION 3.24)

project(XenonLlvmStaticSmoke LANGUAGES CXX)

find_package(LLVM REQUIRED CONFIG)

add_executable(llvm-static-smoke llvm_static_smoke.cpp)
target_compile_features(llvm-static-smoke PRIVATE cxx_std_17)
target_include_directories(llvm-static-smoke PRIVATE ${LLVM_INCLUDE_DIRS})
target_compile_definitions(llvm-static-smoke PRIVATE ${LLVM_DEFINITIONS})
llvm_map_components_to_libnames(XENON_LLVM_SMOKE_LIBRARIES Core Support)
target_link_libraries(llvm-static-smoke PRIVATE ${XENON_LLVM_SMOKE_LIBRARIES})

add_custom_command(TARGET llvm-static-smoke POST_BUILD
  COMMAND "$<TARGET_FILE:llvm-static-smoke>"
  COMMENT "Running static LLVM C API smoke test")
