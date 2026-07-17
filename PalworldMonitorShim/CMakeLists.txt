cmake_minimum_required(VERSION 3.20)
project(PalworldMonitorShim LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(MINHOOK_DIR "${CMAKE_SOURCE_DIR}/../third_party/minhook")

add_library(PalworldMonitorShim SHARED
  src/dllmain.cpp
  src/log.cpp
  src/hooks.cpp
  ${MINHOOK_DIR}/src/buffer.c
  ${MINHOOK_DIR}/src/hook.c
  ${MINHOOK_DIR}/src/trampoline.c
  ${MINHOOK_DIR}/src/hde/hde64.c
)

target_include_directories(PalworldMonitorShim PRIVATE
  ${CMAKE_SOURCE_DIR}/src
  ${MINHOOK_DIR}/include
  ${MINHOOK_DIR}/src
)

target_compile_definitions(PalworldMonitorShim PRIVATE
  WIN32_LEAN_AND_MEAN
  NOMINMAX
  _CRT_SECURE_NO_WARNINGS
)

target_link_libraries(PalworldMonitorShim PRIVATE
  user32
  gdi32
)

set_target_properties(PalworldMonitorShim PROPERTIES
  OUTPUT_NAME "PalworldMonitorShim"
  RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin"
  RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_BINARY_DIR}/bin"
)

if(MSVC)
  # Avoid embedding absolute build-machine paths in Release artifacts.
  target_compile_options(PalworldMonitorShim PRIVATE /experimental:deterministic)
  target_compile_options(PalworldMonitorShim PRIVATE
    "/pathmap:${CMAKE_SOURCE_DIR}=/PalworldMonitor/PalworldMonitorShim"
    "/pathmap:${MINHOOK_DIR}=/PalworldMonitor/third_party/minhook"
  )
  target_link_options(PalworldMonitorShim PRIVATE
    $<$<CONFIG:Release>:/DEBUG:NONE>
  )
endif()
