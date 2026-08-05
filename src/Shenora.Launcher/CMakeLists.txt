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
  src/platform_posix.cpp)

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
  target_link_libraries(shenora_launcher PUBLIC psapi)
else()
  target_compile_options(shenora_launcher PRIVATE -Wall -Wextra -Werror)
  target_compile_options(shenora_launcher PRIVATE $<$<CONFIG:Release>:-Os -ffunction-sections -fdata-sections>)
endif()

# ── The reference launcher, built from the TEMPLATE ─────────────────────────────────────────────────
#
# Building the template is not a demo — it is what keeps the template compiling. A template nobody
# builds is a snippet that rots, and this one is the file every adopter starts from.
add_executable(shenora-launcher template/main.cpp)
target_link_libraries(shenora-launcher PRIVATE shenora_launcher)

if(MSVC)
  target_compile_options(shenora-launcher PRIVATE /utf-8 /W4 /WX /permissive-)
  target_compile_options(shenora-launcher PRIVATE $<$<CONFIG:Release>:/O1 /GL>)
else()
  target_compile_options(shenora-launcher PRIVATE -Wall -Wextra -Werror)
  target_compile_options(shenora-launcher PRIVATE $<$<CONFIG:Release>:-Os -ffunction-sections -fdata-sections>)
endif()

if(MSVC)
  target_link_options(shenora-launcher PRIVATE $<$<CONFIG:Release>:/LTCG /OPT:REF /OPT:ICF>)
  # Statically link the CRT so the binary runs on a machine with no VC++ redistributable — a launcher
  # that needs a redistributable installed has the same bootstrap problem it exists to solve.
  set_property(TARGET shenora_launcher shenora-launcher
               PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
else()
  target_link_options(shenora-launcher PRIVATE $<$<CONFIG:Release>:-Wl,--gc-sections -s>)
endif()

# Report the size at build time. D50 records the expected bands as BANDS nobody had measured; this is
# how they stop being guesses.
add_custom_command(TARGET shenora-launcher POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E echo "shenora-launcher built: $<TARGET_FILE:shenora-launcher>")
