#!/usr/bin/env bash
# Runs tests/Shenora.Tests.Linux, the Chromium shell's Linux code, on a private Xvfb display. Extra arguments go to
# `dotnet test` (a --filter, say). `node devtools/dev.mjs linux-tests` is how it is run from Windows, through WSL.
#
# Needs dotnet, Xvfb (the xvfb and x11-xkb-utils packages) and, for the web panel's test, openbox. Where there is no root to install them, point
# SHENORA_LINUX_SYSROOT at a folder holding the packages unpacked (usr/bin/Xvfb, usr/bin/xkbcomp, the libraries under
# usr/lib/x86_64-linux-gnu): Xvfb finds xkbcomp only at /usr/bin, so with no xkbcomp there it is lent one inside a user
# namespace, along with the folders it writes its compiled keymap to. Nothing outside the repository's devtools/_build
# is written.
set -u
root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[ -x "$HOME/.dotnet/dotnet" ] && PATH="$HOME/.dotnet:$PATH"
sysroot=${SHENORA_LINUX_SYSROOT:-}
[ -n "$sysroot" ] && PATH="$sysroot/usr/bin:$PATH"
scratch=$root/devtools/_build
mkdir -p "$scratch"

if [ "${SHENORA_LINUX_TESTS_INNER:-}" != 1 ]; then
  export SHENORA_LINUX_TESTS_INNER=1
  if [ -z "$sysroot" ] || [ -x /usr/bin/xkbcomp ]; then exec "$0" "$@"; fi
  mkdir -p "$scratch/xkbcomp-lend"
  cp "$sysroot/usr/bin/xkbcomp" "$scratch/xkbcomp-lend/"
  # The overlay's first mount can fail while the kernel loads the module; a second try finds it.
  # The tests then run as the caller, in a user namespace of their own inside the first: root there, Chromium refuses to start.
  uid=$(id -u) gid=$(id -g)
  for _ in 1 2 3; do
    unshare -Urm bash -c '
      mount -t overlay overlay -o lowerdir="$1/devtools/_build/xkbcomp-lend:/usr/bin" /usr/bin || exit 97
      mount -t tmpfs tmpfs /tmp && mount -t tmpfs tmpfs /var/lib && mkdir -p /var/lib/xkb || exit 98
      uid=$2 gid=$3
      shift 3
      exec unshare --user --map-user="$uid" --map-group="$gid" -- "$0" "$@"' "$0" "$root" "$uid" "$gid" "$@"
    rc=$?
    [ $rc -ne 97 ] && exit $rc
    sleep 1
  done
  echo "linux-tests: could not lend Xvfb an xkbcomp (an overlay mount in a user namespace)." >&2
  exit 97
fi

command -v Xvfb >/dev/null || { echo "linux-tests: Xvfb is not installed (see this script's header)." >&2; exit 2; }
display=$(mktemp "$scratch/xdisplay.XXXXXX")
LD_LIBRARY_PATH="${sysroot:+$sysroot/usr/lib/x86_64-linux-gnu}" \
  Xvfb -screen 0 1280x800x24 -nolisten tcp -displayfd 5 5>"$display" >"$scratch/xvfb.log" 2>&1 &
xvfb=$!
for _ in $(seq 100); do [ -s "$display" ] && break; sleep 0.1; done
if [ ! -s "$display" ]; then
  echo "linux-tests: Xvfb did not start:" >&2
  cat "$scratch/xvfb.log" >&2
  kill $xvfb 2>/dev/null
  rm -f "$display"
  exit 2
fi
export DISPLAY=:$(tr -d '[:space:]' <"$display")
# The Chromium sample, which the web panel's test runs on real CEF: laid out beside CEF's runtime, so it needs the kit's
# Linux helper (`dev.mjs cef-native --rid linux-x64`, which needs a C compiler) staged, and the first build fetches CEF.
# A build that fails leaves SHENORA_LINUX_SAMPLE unset, and that test says so, rather than the rest not running.
sample=$scratch/linux-sample/bin/Shenora.Sample.Chromium/debug_linux-x64
if dotnet build samples/Shenora.Sample.Chromium/Shenora.Sample.Chromium.csproj -r linux-x64 --artifacts-path "$scratch/linux-sample" -v quiet >"$scratch/linux-sample-build.log" 2>&1; then
  export SHENORA_LINUX_SAMPLE=$sample/Shenora.Sample.Chromium
else
  echo "linux-tests: the Chromium sample did not build (devtools/_build/linux-sample-build.log); the web panel test will fail." >&2
fi
[ -n "$sysroot" ] && export SHENORA_LINUX_SYSROOT=$sysroot
dotnet test tests/Shenora.Tests.Linux/Shenora.Tests.Linux.csproj --artifacts-path "$scratch/linux-tests" "$@"
rc=$?
kill $xvfb 2>/dev/null
rm -f "$display"
exit $rc
