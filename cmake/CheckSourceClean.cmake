if(NOT DEFINED REPOSITORY_ROOT)
  message(FATAL_ERROR "REPOSITORY_ROOT is required")
endif()

set(_source_roots xenon/src xenon/tests pipelines)
set(_violations)
foreach(_relative_root IN LISTS _source_roots)
  set(_root "${REPOSITORY_ROOT}/${_relative_root}")
  if(NOT EXISTS "${_root}")
    continue()
  endif()
  file(GLOB_RECURSE _entries LIST_DIRECTORIES TRUE RELATIVE "${_root}" "${_root}/*")
  foreach(_entry IN LISTS _entries)
    if(_entry MATCHES "(^|/)(bin|obj|CMakeFiles)(/|$)" OR
       _entry MATCHES "(^|/)(CMakeCache\\.txt|[^/]*\\.ninja)$")
      list(APPEND _violations "${_relative_root}/${_entry}")
    endif()
  endforeach()
endforeach()

# The upstream LLVM repository intentionally contains tracked fixture directories
# named bin/ and obj/. Git status distinguishes those sources from files generated
# by an accidental in-source LLVM/CMake build.
find_program(GIT_EXECUTABLE git REQUIRED)
set(_llvm_source "${REPOSITORY_ROOT}/llvm/llvm-project")
if(EXISTS "${_llvm_source}/.git")
  execute_process(
    COMMAND "${GIT_EXECUTABLE}" -C "${_llvm_source}" status --porcelain --untracked-files=all
    RESULT_VARIABLE _llvm_status_result
    OUTPUT_VARIABLE _llvm_status
    ERROR_VARIABLE _llvm_status_error
    OUTPUT_STRIP_TRAILING_WHITESPACE)
  if(NOT _llvm_status_result EQUAL 0)
    message(FATAL_ERROR "Could not inspect LLVM source tree: ${_llvm_status_error}")
  endif()
  if(_llvm_status)
    string(REPLACE "\n" "\n  llvm/llvm-project/" _llvm_status "${_llvm_status}")
    list(APPEND _violations "llvm/llvm-project/${_llvm_status}")
  endif()
endif()

if(_violations)
  list(JOIN _violations "\n  " _violation_text)
  message(FATAL_ERROR "Generated artifacts were found in source directories:\n  ${_violation_text}")
endif()
message(STATUS "Source directories are clean; generated artifacts are confined to build/")
