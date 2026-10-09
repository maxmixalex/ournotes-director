#!/bin/bash
set -euo pipefail
# Run on the Ubuntu 24.04 build host. Does not install or modify host libraries.
package=$(realpath "$1")
ffmpeg_build=$(realpath "$2")
mkdir -p "$package/native/dri" "$package/licenses/system"
native="$package/native"
declare -A copied=()
copy_library() {
    local file="$1" name owner
    name=$(basename "$file")
    case "$name" in libc.so.*|libm.so.*|libpthread.so.*|libdl.so.*|librt.so.*|ld-linux*|libresolv.so.*) return;; esac
    if [[ -n "${copied[$name]:-}" ]]; then return; fi
    copied[$name]=1
    cp -L "$file" "$native/$name"
    owner=$(dpkg-query -S "$(readlink -f "$file")" 2>/dev/null | head -1 | cut -d: -f1 || true)
    if [[ -n "$owner" && -f "/usr/share/doc/$owner/copyright" ]]; then cp "/usr/share/doc/$owner/copyright" "$package/licenses/system/$owner.txt"; fi
}
for entry in /usr/lib/x86_64-linux-gnu/libGL.so.1 /usr/lib/x86_64-linux-gnu/libEGL.so.1 /usr/lib/x86_64-linux-gnu/libEGL_mesa.so.0 /usr/lib/x86_64-linux-gnu/libGLX_mesa.so.0 /usr/lib/x86_64-linux-gnu/dri/swrast_dri.so; do
    copy_library "$entry"
    while read -r dependency; do copy_library "$dependency"; done < <(ldd "$entry" | awk '$2 == "=>" && $3 ~ /^\// {print $3}')
done
cp -L /usr/lib/x86_64-linux-gnu/dri/swrast_dri.so "$native/dri/swrast_dri.so"
printf '%s\n' '{"file_format_version":"1.0.0","ICD":{"library_path":"libEGL_mesa.so.0"}}' > "$native/egl-vendor.json"
cp "$ffmpeg_build/bin/ffmpeg" "$ffmpeg_build/bin/ffprobe" "$package/"
cp -r "$ffmpeg_build/doc" "$package/licenses/ffmpeg-doc"
chmod +x "$package/advkit" "$package/ffmpeg" "$package/ffprobe"
