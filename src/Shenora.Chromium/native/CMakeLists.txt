# The Chromium shell's native shim (shim_win.cpp): the DLL CEF's bootstrap.exe loads under the app's name.
#
#   node devtools/dev.mjs cef-native        builds it against the pinned CEF build and the SDK's nethost
#
# It needs CEF's headers and import library (CEF_ROOT, the distribution the CEF tools cache), nethost's
# static library (NETHOST_DIR, the .NET SDK's Microsoft.NETCore.App.Host.<rid> pack), and the Stable API
# version the binding was generated for (CEF_API_VERSION, from cef.json).
cmake_minimum_required(VERSION 3.20)
project(shenora_chromium_shim LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

foreach(required CEF_ROOT NETHOST_DIR CEF_API_VERSION)
  if(NOT ${required})
    message(FATAL_ERROR "${required} is not set; build through `node devtools/dev.mjs cef-native`.")
  endif()
endforeach()

if(NOT WIN32)
  message(FATAL_ERROR "The shim is Windows' alone; macOS and Linux build their helper (helper_mac.c, helper_linux.c) through `node devtools/dev.mjs cef-native`, without CMake.")
endif()

add_library(shenora_chromium_shim SHARED shim_win.cpp)
target_include_directories(shenora_chromium_shim PRIVATE "${CEF_ROOT}" "${NETHOST_DIR}")
target_compile_definitions(shenora_chromium_shim PRIVATE CEF_API_VERSION=${CEF_API_VERSION} NETHOST_USE_AS_STATIC UNICODE _UNICODE)
target_link_libraries(shenora_chromium_shim PRIVATE "${CEF_ROOT}/Release/libcef.lib" "${NETHOST_DIR}/libnethost.lib" shell32 user32)
# /utf-8: BOM-less UTF-8 sources on a CJK-locale box are otherwise read as codepage 936, and a comment's
# em dash fails C4819 under /WX (the same trap src/Shenora.Launcher documents).
target_compile_options(shenora_chromium_shim PRIVATE /utf-8 /W4 /WX /permissive- $<$<CONFIG:Release>:/O1 /GL>)
target_link_options(shenora_chromium_shim PRIVATE $<$<CONFIG:Release>:/LTCG>)
# libnethost.lib is built against the STATIC CRT, so the shim links the same one.
set_property(TARGET shenora_chromium_shim PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
