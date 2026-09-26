# Escape a raw value once before inserting it into an XML attribute.
function(xenon_xml_escape output_var value)
  string(REPLACE "&" "&amp;" _escaped "${value}")
  string(REPLACE "<" "&lt;" _escaped "${_escaped}")
  string(REPLACE ">" "&gt;" _escaped "${_escaped}")
  string(REPLACE "\"" "&quot;" _escaped "${_escaped}")
  string(REPLACE "'" "&apos;" _escaped "${_escaped}")
  set(${output_var} "${_escaped}" PARENT_SCOPE)
endfunction()
