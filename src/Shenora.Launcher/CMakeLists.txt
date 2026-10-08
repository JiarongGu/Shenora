# Shenora launcher — the C++ library and its reference launcher.
#
# ONE source tree for Windows and Linux (D50). Everything portable is std::filesystem; the four things
# that are not live behind include/shenora/platform.hpp with one implementation per family, and CI
# builds BOTH on every change so the Linux half cannot rot into a claim nothing compiles.
#
#   cmake -S src/Shenora.Launcher -B build -DCMAKE_BUILD_TYPE=Release
#   cmake --build build --config Release
#
# `node devtools/dev.mjs launcher` does that for you and then runs the conformance harness.
cmake_minimum_required(VERSION 3.20)
project(shenora_launcher LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)          # std::filesystem, and nothing newer is needed
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# ── The library ─────────────────────────────────────────────────────────────────────────────────────
add_library(shenora_launcher STATIC
  src/manifest.cpp
  src/updater.cpp
  src/platform_win32.cpp
  src/platform_posix.cpp
  src/startup_screen_compose.cpp
  src/startup_screen_win32.cpp
  src/startup_screen_x11.cpp
  src/png_stb.cpp)

# The template runs the update apply on a worker thread while a startup screen keeps moving.
find_package(Threads REQUIRED)
target_link_libraries(shenora_launcher PUBLIC Threads::Threads)

# shenora_launcher_startup_screen(): an adopter's launcher compiles its screen in with it.
include(${CMAKE_CURRENT_LIST_DIR}/cmake/ShenoraStartupScreen.cmake)

# Both platform files are always compiled; each is a no-op on the other OS via its own #ifdef. That is
# deliberate rather than a CMake conditional: a source file excluded by the build system is one nobody
# notices has stopped compiling, and the Linux half existing-but-broken is the exact failure D50's
# "Linux for a future need" is meant to prevent.
#
# ⚠ COMPILING BOTH FILES IS NOT THE SAME AS COMPILING BOTH TARGETS, which the first release run proved:
# platform_posix.cpp compiled clean under MSVC and failed under gcc with "'all_of' is not a member of
# 'std'", because MSVC drags most of the standard library in through other headers and gcc does not.
# `#ifdef`-guarded bodies are only checked by the compiler that takes that branch.
#
# `node devtools/dev.mjs launcher --posix` builds this file with gcc in a container, so a Windows box can
# prove the platform it cannot compile. Run it whenever you touch the POSIX half — D5 means there is no
# push CI, so a release is otherwise the first thing that compiles it.
target_include_directories(shenora_launcher PUBLIC include)
# PUBLIC, so an adopter's launcher that add_subdirectory()s this library compiles its headers as C++17 too: the
# CMAKE_CXX_STANDARD above is this directory's, and MSVC's default (C++14) has no std::filesystem.
target_compile_features(shenora_launcher PUBLIC cxx_std_17)

if(MSVC)
  # /utf-8 is NOT optional on this family's machines, and it is the C++ half of a trap the repo already
  # documents for C# (`<CodePage>65001</CodePage>` in src/Directory.Build.props). These sources are
  # BOM-less UTF-8; on a CJK-locale Windows box MSVC reads them as the ANSI codepage (936 here) and
  # every file with a — or a ⚠ in a comment fails with C4819, which under /WX is a hard build error.
  # Measured on the first build of this library.
  target_compile_options(shenora_launcher PRIVATE /utf-8 /W4 /WX /permissive-)
  # SIZE is a stated requirement (D50), so it is a build setting rather than a hope:
  #   /O1  optimise for size   /GL + /LTCG  whole-program  /GS-  no stack cookies in a trusted-input tool
  target_compile_options(shenora_launcher PRIVATE $<$<CONFIG:Release>:/O1 /GL>)
  # shell32: CommandLineToArgvW; user32: MessageBoxW and the screen's window; the rest: the startup screen (GDI's DIB,
  # DWM's transitions, shcore's per-monitor DPI, WIC's PNG decoder over COM).
  target_link_libraries(shenora_launcher PUBLIC psapi shell32 user32 gdi32 dwmapi shcore windowscodecs ole32)
else()
  target_compile_options(shenora_launcher PRIVATE -Wall -Wextra -Werror)
  target_compile_options(shenora_launcher PRIVATE $<$<CONFIG:Release>:-Os -ffunction-sections -fdata-sections>)
  # The startup screen loads libX11 (and libXrandr) with dlopen: their HEADERS are needed to build (libx11-dev,
  # libxrandr-dev), and nothing at link time beyond libdl. stb_image is third-party code: its warnings are not ours.
  target_link_libraries(shenora_launcher PUBLIC ${CMAKE_DL_LIBS})
  find_path(SHENORA_X11_INCLUDE_DIR X11/extensions/Xrandr.h)
  if(NOT SHENORA_X11_INCLUDE_DIR)
    message(FATAL_ERROR "The launcher builds against the X11 headers (it loads libX11 at run time, only to show a startup "
                        "screen): install libx11-dev and libxrandr-dev (Debian/Ubuntu), libX11-devel and libXrandr-devel "
                        "(Fedora).")
  endif()
  set_source_files_properties(src/png_stb.cpp PROPERTIES COMPILE_OPTIONS "-w")
endif()

# ── The reference launcher, built from the TEMPLATE ─────────────────────────────────────────────────
#
# Building the template is not a demo — it is what keeps the template compiling. A template nobody
# builds is a snippet that rots, and this one is the file every adopter starts from.
function(shenora_launcher_executable name)
  add_executable(${name} template/main.cpp)
  target_link_libraries(${name} PRIVATE shenora_launcher)
  if(MSVC)
    target_compile_options(${name} PRIVATE /utf-8 /W4 /WX /permissive-)
    target_compile_options(${name} PRIVATE $<$<CONFIG:Release>:/O1 /GL>)
    target_link_options(${name} PRIVATE $<$<CONFIG:Release>:/LTCG /OPT:REF /OPT:ICF>)
    # Statically link the CRT so the binary runs on a machine with no VC++ redistributable — a launcher
    # that needs a redistributable installed has the same bootstrap problem it exists to solve.
    set_property(TARGET ${name} PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  else()
    target_compile_options(${name} PRIVATE -Wall -Wextra -Werror)
    target_compile_options(${name} PRIVATE $<$<CONFIG:Release>:-Os -ffunction-sections -fdata-sections>)
    target_link_options(${name} PRIVATE $<$<CONFIG:Release>:-Wl,--gc-sections -s>)
  endif()
  if(WIN32)
    # A GUI-subsystem executable: a console one opens a console window on a double-click, the opposite of a smooth
    # start. main() stays the entry point; what a person must read goes to a message box (show_error).
    set_target_properties(${name} PROPERTIES WIN32_EXECUTABLE ON)
    if(MSVC)
      target_link_options(${name} PRIVATE /ENTRY:mainCRTStartup)
    endif()
  endif()
endfunction()

if(MSVC)
  set_property(TARGET shenora_launcher PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

shenora_launcher_executable(shenora-launcher)

# The conformance harness's launchers: the template with a small startup screen compiled in. Not shipped.
option(SHENORA_LAUNCHER_TESTS "Build the conformance harness's startup-screen launchers" OFF)
if(SHENORA_LAUNCHER_TESTS)
  shenora_launcher_executable(shenora-launcher-screen)
  shenora_launcher_startup_screen(shenora-launcher-screen IMAGE "${CMAKE_CURRENT_LIST_DIR}/tests/screen.png"
    WIDTH 200 HEIGHT 100 BACKGROUND "#FF102030" PROGRESS_BAR ON PROGRESS_COLOR "#FFFF0000" CORNERS ROUNDED TIMEOUT_SECONDS 3)
  shenora_launcher_executable(shenora-launcher-wide)
  shenora_launcher_startup_screen(shenora-launcher-wide IMAGE "${CMAKE_CURRENT_LIST_DIR}/tests/wide.png"
    WIDTH 200 HEIGHT 100 BACKGROUND "#FF102030" CORNERS SQUARE TIMEOUT_SECONDS 3)
  # The app those launchers start: records its arguments and closes, leaves or ignores the screen.
  add_executable(shenora-fake-app tests/fake_app.cpp)
  target_link_libraries(shenora-fake-app PRIVATE ${CMAKE_DL_LIBS})
  if(MSVC)
    target_compile_options(shenora-fake-app PRIVATE /utf-8 /W4 /WX)
    target_compile_definitions(shenora-fake-app PRIVATE _CRT_SECURE_NO_WARNINGS)   # getenv, in a test-only app
    set_property(TARGET shenora-fake-app PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  else()
    target_compile_options(shenora-fake-app PRIVATE -Wall -Wextra -Werror)
  endif()
endif()

# Report the size at build time. D50 records the expected bands as BANDS nobody had measured; this is
# how they stop being guesses.
add_custom_command(TARGET shenora-launcher POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E echo "shenora-launcher built: $<TARGET_FILE:shenora-launcher>")
