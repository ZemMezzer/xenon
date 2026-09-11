if(NOT DEFINED LLVM_BUILD_DIRECTORY OR NOT DEFINED OUTPUT_FILE)
  message(FATAL_ERROR "LLVM_BUILD_DIRECTORY and OUTPUT_FILE are required")
endif()

set(_llvm_config_candidates
  "${LLVM_BUILD_DIRECTORY}/bin/llvm-config${CMAKE_EXECUTABLE_SUFFIX}"
  "${LLVM_BUILD_DIRECTORY}/Release/bin/llvm-config.exe"
  "${LLVM_BUILD_DIRECTORY}/bin/Release/llvm-config.exe")
foreach(_candidate IN LISTS _llvm_config_candidates)
  if(EXISTS "${_candidate}")
    set(_llvm_config "${_candidate}")
    break()
  endif()
endforeach()
if(NOT _llvm_config)
  message(FATAL_ERROR "Could not find llvm-config below ${LLVM_BUILD_DIRECTORY}")
endif()

execute_process(
  COMMAND "${_llvm_config}" --link-static --libfiles
    core passes x86codegen aarch64codegen
  RESULT_VARIABLE _libraries_result
  OUTPUT_VARIABLE _libraries_output
  ERROR_VARIABLE _libraries_error
  OUTPUT_STRIP_TRAILING_WHITESPACE)
if(NOT _libraries_result EQUAL 0)
  message(FATAL_ERROR "llvm-config --libfiles failed: ${_libraries_error}")
endif()

execute_process(
  COMMAND "${_llvm_config}" --link-static --system-libs
    core passes x86codegen aarch64codegen
  RESULT_VARIABLE _system_result
  OUTPUT_VARIABLE _system_output
  ERROR_VARIABLE _system_error
  OUTPUT_STRIP_TRAILING_WHITESPACE)
if(NOT _system_result EQUAL 0)
  message(FATAL_ERROR "llvm-config --system-libs failed: ${_system_error}")
endif()

separate_arguments(_libraries NATIVE_COMMAND "${_libraries_output}")
separate_arguments(_system_libraries NATIVE_COMMAND "${_system_output}")

set(_props "<Project>\n  <ItemGroup Condition=\"'$(XenonStaticLLVM)' == 'true'\">\n")
foreach(_library IN LISTS _libraries)
  if(NOT EXISTS "${_library}")
    message(FATAL_ERROR "llvm-config reported a static library that does not exist: ${_library}")
  endif()
  string(REPLACE "&" "&amp;" _library_xml "${_library}")
  string(REPLACE "\"" "&quot;" _library_xml "${_library_xml}")
  string(APPEND _props "    <NativeLibrary Include=\"${_library_xml}\" />\n")
endforeach()
foreach(_system_library IN LISTS _system_libraries)
  string(REPLACE "&" "&amp;" _system_xml "${_system_library}")
  string(REPLACE "\"" "&quot;" _system_xml "${_system_xml}")
  string(APPEND _props "    <LinkerArg Include=\"${_system_xml}\" />\n")
endforeach()
string(APPEND _props "  </ItemGroup>\n</Project>\n")

get_filename_component(_output_directory "${OUTPUT_FILE}" DIRECTORY)
file(MAKE_DIRECTORY "${_output_directory}")
file(WRITE "${OUTPUT_FILE}.tmp" "${_props}")
file(RENAME "${OUTPUT_FILE}.tmp" "${OUTPUT_FILE}")
message(STATUS "Wrote NativeAOT static LLVM inputs to ${OUTPUT_FILE}")
