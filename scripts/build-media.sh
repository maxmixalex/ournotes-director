#!/usr/bin/env bash
# Build the media programs used by advkit. No third-party shared libraries,
# package-manager media binaries, or floating "latest" downloads are used.
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
repo_dir=$(cd -- "$script_dir/.." && pwd)
rid=${1:?Usage: build-media.sh <linux-x64|win-x64> <publish-directory>}
output=${2:?Usage: build-media.sh <linux-x64|win-x64> <publish-directory>}
case "$rid:$(uname -s)" in
    linux-x64:Linux) suffix=; linker_flags= ;;
    win-x64:MINGW*|win-x64:MSYS*) suffix=.exe; linker_flags=-static ;;
    *) printf 'Build %s on its target OS (Windows uses MSYS2 UCRT64).\n' "$rid" >&2; exit 2 ;;
esac
for tool in gcc make nasm pkg-config curl sha256sum tar python3; do
    command -v "$tool" >/dev/null || { printf 'Missing media build tool: %s\n' "$tool" >&2; exit 2; }
done
mkdir -p -- "$output"
output=$(cd -- "$output" && pwd)
cache=${OURNOTES_MEDIA_CACHE:-"$repo_dir/artifacts/media-cache"}
mkdir -p -- "$cache"
cache=$(cd -- "$cache" && pwd)
# Upstream makefiles do not support spaces in their build/prefix directories.
# The final publish directory is independent and may contain spaces.
if [[ "$cache" == *' '* ]]; then
    printf 'Set OURNOTES_MEDIA_CACHE to a build directory without spaces.\n' >&2
    exit 2
fi
bundle="$cache/bundle/$rid"
fingerprint=$(cat "$script_dir/build-media.sh" "$script_dir/media-sources.lock" | sha256sum | cut -d ' ' -f 1)
if [[ -f "$bundle/.build-key" && "$(cat "$bundle/.build-key")" == "$fingerprint" ]] &&
   (cd "$bundle" && sha256sum --check --status media-files.sha256); then
    printf 'Reusing verified media bundle for %s.\n' "$rid"
else
    jobs=${OURNOTES_BUILD_JOBS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf '4')}
    [[ "$jobs" =~ ^[1-9][0-9]*$ ]] || { printf 'Invalid OURNOTES_BUILD_JOBS.\n' >&2; exit 2; }
    work="$cache/build/$rid"
    rm -rf -- "$work" "$bundle"
    mkdir -p -- "$work" "$bundle/tools" "$bundle/licenses/ffmpeg" "$bundle/media-sources" "$cache/downloads"
    prefix="$work/prefix"
    mkdir -p -- "$prefix/lib/pkgconfig" "$prefix/include"
    while read -r component archive expected url; do
        [[ -z "$component" || "$component" == \#* ]] && continue
        download="$cache/downloads/$archive"
        if [[ ! -f "$download" ]] || [[ "$(sha256sum "$download" | cut -d ' ' -f 1)" != "$expected" ]]; then
            # Published source bundles can rebuild entirely from their included
            # archives; source checkouts acquire the same verified bytes once.
            if [[ -f "$script_dir/$archive" ]]; then
                cp -- "$script_dir/$archive" "$download.part"
            else
                printf 'Downloading %s sources.\n' "$component"
                curl --fail --location --retry 3 --connect-timeout 30 --max-time 300 \
                    "$url" --output "$download.part"
            fi
            [[ "$(sha256sum "$download.part" | cut -d ' ' -f 1)" == "$expected" ]] || {
                rm -f -- "$download.part"
                printf 'Source checksum mismatch: %s\n' "$archive" >&2
                exit 1
            }
            mv -f -- "$download.part" "$download"
        fi
        mkdir -p -- "$work/$component"
        tar --no-same-owner -xf "$download" -C "$work/$component" --strip-components=1
        cp -- "$download" "$bundle/media-sources/$archive"
    done < "$script_dir/media-sources.lock"
    export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
    export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
    export CFLAGS='-O2 -fPIC'
    (
        cd -- "$work/zlib"
        if [[ "$rid" == win-x64 ]]; then
            make -f win32/Makefile.gcc -j "$jobs" libz.a
            cp libz.a "$prefix/lib/"
            cp zlib.h zconf.h "$prefix/include/"
            printf '%s\n' "prefix=$prefix" 'exec_prefix=${prefix}' 'libdir=${prefix}/lib' \
                'includedir=${prefix}/include' '' 'Name: zlib' 'Description: zlib compression library' \
                'Version: 1.3.1' 'Libs: -L${libdir} -lz' 'Cflags: -I${includedir}' > "$prefix/lib/pkgconfig/zlib.pc"
        else
            ./configure --static --prefix="$prefix"
            make -j "$jobs"
            make install
        fi
    )
    (
        cd -- "$work/x264"
        ./configure --prefix="$prefix" --enable-static --enable-pic --disable-cli --disable-opencl
        make -j "$jobs"
        make install-lib-static
    )
    (
        cd -- "$work/ffmpeg"
        ./configure --prefix="$prefix" --disable-autodetect --disable-debug --disable-doc \
            --disable-ffplay --disable-shared --enable-static --enable-gpl --enable-libx264 --enable-zlib \
            --disable-network --disable-devices --disable-encoders \
            --enable-encoder=libx264,prores_ks,ffv1,png,rawvideo,wrapped_avframe,aac,pcm_s16le,pcm_s24le,pcm_f32le,movtext,srt,subrip \
            --pkg-config-flags=--static --extra-cflags="-I$prefix/include" \
            --extra-ldflags="-L$prefix/lib $linker_flags"
        # Preserve assembler objects until linking; FFmpeg strips the final
        # executables itself, so intermediate in-place stripping is unnecessary.
        make -j "$jobs" ASMSTRIPFLAGS= ffmpeg"$suffix" ffprobe"$suffix"
        cp -- ffmpeg"$suffix" ffprobe"$suffix" "$bundle/tools/"
        cp -- COPYING.GPLv2 COPYING.GPLv3 COPYING.LGPLv2.1 COPYING.LGPLv3 LICENSE.md "$bundle/licenses/ffmpeg/"
        cp -- ffbuild/config.mak "$bundle/media-sources/ffmpeg-config.mak"
    )
    cp -- "$work/x264/COPYING" "$bundle/licenses/ffmpeg/x264-COPYING"
    cp -- "$work/zlib/README" "$bundle/licenses/ffmpeg/zlib-README"
    cp -- "$script_dir/build-media.sh" "$script_dir/media-sources.lock" "$bundle/media-sources/"
    chmod +x "$bundle/tools/ffmpeg$suffix" "$bundle/tools/ffprobe$suffix" "$bundle/media-sources/build-media.sh"
    "$bundle/tools/ffmpeg$suffix" -version > "$bundle/licenses/ffmpeg/build-version.txt"
    "$bundle/tools/ffmpeg$suffix" -buildconf >> "$bundle/licenses/ffmpeg/build-version.txt" 2>&1
    cat > "$bundle/licenses/ffmpeg/README.txt" <<'NOTICE'
This distribution includes FFmpeg 8.0.3 with libx264 and zlib, built from the
unmodified archives included in media-sources/. FFmpeg and x264 are GPL software;
their licenses and the zlib notice are included in this directory. These programs
are invoked as separate processes. They are not linked into advkit or Live2D.

The complete FFmpeg, x264 and zlib source archives, their verified checksums,
the exact build script and the generated FFmpeg configuration are included in
media-sources/. Run build-media.sh <linux-x64|win-x64> <output-directory> there
to rebuild with GCC, make, NASM, pkg-config, Python 3 and a POSIX shell. Windows
builds use MSYS2 UCRT64. The source archives are reused without network access.

The supplied tools support local files/pipes and the codecs used by advkit,
including H.264, ProRes, FFV1, PNG, AAC and PCM output, with FFmpeg's built-in
decoders. Hardware acceleration, capture devices and network protocols are
disabled. --ffmpeg and --ffprobe can select another installation explicitly.
NOTICE
    python3 - "$bundle" "$rid" <<'PY'
import hashlib, json, pathlib, sys
root, rid = pathlib.Path(sys.argv[1]), sys.argv[2]
files = {}
for path in sorted(root.rglob('*')):
    if path.is_file():
        files[path.relative_to(root).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
(root / 'media-dependencies.json').write_text(json.dumps({
    'format': 'ournotes.media-dependencies/1', 'rid': rid,
    'ffmpeg': '8.0.3', 'x264': '31e19f92f00c7003fa115047ce50978bc98c3a0d', 'zlib': '1.3.1',
    'files': files,
}, indent=2) + '\n', encoding='utf-8')
files['media-dependencies.json'] = hashlib.sha256((root / 'media-dependencies.json').read_bytes()).hexdigest()
(root / 'media-files.sha256').write_text(''.join(f'{value}  {name}\n' for name, value in sorted(files.items())), encoding='utf-8')
PY
    printf '%s\n' "$fingerprint" > "$bundle/.build-key"
fi
# Keep the private cache marker out of the user package.
cp -R -- "$bundle/tools" "$bundle/licenses" "$bundle/media-sources" "$output/"
cp -- "$bundle/media-dependencies.json" "$bundle/media-files.sha256" "$output/"
printf 'Bundled FFmpeg and FFprobe in %s/tools.\n' "$output"
