cmake_minimum_required(VERSION 3.24)

include("${CMAKE_CURRENT_LIST_DIR}/../XenonPlatform.cmake")
include("${CMAKE_CURRENT_LIST_DIR}/../XmlEscape.cmake")

function(assert_equal actual expected description)
  if(NOT "${actual}" STREQUAL "${expected}")
    message(FATAL_ERROR "${description}: expected '${expected}', got '${actual}'")
  endif()
endfunction()

foreach(_case IN ITEMS
    "win_x86|win-x86|x86|Win32|xenon.exe"
    "win_x64|win-x64|x86_64|x64|xenon.exe"
    "win_arm64|win-arm64|arm64|ARM64|xenon.exe"
    "darwin_arm64|osx-arm64|arm64||xenon")
  string(REPLACE "|" ";" _fields "${_case}")
  list(LENGTH _fields _field_count)
  assert_equal("${_field_count}" "5" "${_case} field count")
  list(GET _fields 0 _platform)
  list(GET _fields 1 _expected_rid)
  list(GET _fields 2 _expected_arch)
  list(GET _fields 3 _expected_generator)
  list(GET _fields 4 _expected_executable)
  xenon_platform_properties("${_platform}" _rid _arch _generator _executable)
  assert_equal("${_rid}" "${_expected_rid}" "${_platform} RID")
  assert_equal("${_arch}" "${_expected_arch}" "${_platform} architecture")
  assert_equal("${_generator}" "${_expected_generator}" "${_platform} generator")
  assert_equal("${_executable}" "${_expected_executable}" "${_platform} executable")
endforeach()

foreach(_case IN ITEMS
    "Windows|AMD64||x64|win_x64"
    "Windows|AMD64|Win32|x64|win_x86"
    "Windows|AMD64|ARM64|x64|win_arm64"
    "Windows|ARM64|||win_arm64"
    "Windows|x86|||win_x86"
    "Darwin|arm64|||darwin_arm64")
  string(REPLACE "|" ";" _fields "${_case}")
  list(LENGTH _fields _field_count)
  assert_equal("${_field_count}" "5" "${_case} field count")
  list(GET _fields 0 _host)
  list(GET _fields 1 _processor)
  list(GET _fields 2 _generator)
  list(GET _fields 3 _vs_platform)
  list(GET _fields 4 _expected)
  xenon_detect_platform(_detected "${_host}" "${_processor}" "${_generator}" "${_vs_platform}")
  assert_equal("${_detected}" "${_expected}" "${_case} detection")
endforeach()

xenon_xml_escape(_escaped "A&B<C>D\"E'F")
assert_equal("${_escaped}" "A&amp;B&lt;C&gt;D&quot;E&apos;F" "XML attribute escaping")
message(STATUS "Xenon build-system mapping and XML checks passed")
