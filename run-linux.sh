#!/bin/sh
set -eu
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
export PATH="$here:$here/bin:$PATH"
# Set OURNOTES_SYSTEM_GL=1 to use an already configured host EGL/OpenGL driver.
if [ "${OURNOTES_SYSTEM_GL:-0}" != 1 ] && [ -f "$here/native/egl-vendor.json" ]; then
    export LD_LIBRARY_PATH="$here/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    export LIBGL_DRIVERS_PATH="$here/native/dri"
    export __EGL_VENDOR_LIBRARY_FILENAMES="$here/native/egl-vendor.json"
    export LIBGL_ALWAYS_SOFTWARE=1
    export GALLIUM_DRIVER=llvmpipe
    export LP_NUM_THREADS="${LP_NUM_THREADS:-8}"
fi
export CUBISM_NATIVE_SDK="${CUBISM_NATIVE_SDK:-$here/CubismSdkForNative-5-r.5}"
for executable in "$here/artifacts/linux-x64/advkit" "$here/advkit" "$here/bin/advkit"; do
    if [ -x "$executable" ]; then exec "$executable" "$@"; fi
done
printf '%s\n' 'No native advkit found. Run ./build.sh or extract the Linux CI package.' >&2
exit 3
