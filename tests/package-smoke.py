#!/usr/bin/env python3
"""Exercise the complete extracted release with no installed SDK, .NET or FFmpeg."""
import argparse
import array
import hashlib
import json
import math
import os
from pathlib import Path
import struct
import subprocess
import tarfile
import tempfile
import wave
from zipfile import ZipFile


def run(arguments, cwd, environment, *, binary=False):
    result = subprocess.run([str(argument) for argument in arguments], cwd=cwd, env=environment,
                            capture_output=True, timeout=90)
    if result.returncode:
        raise AssertionError(f"{arguments} failed ({result.returncode})\n"
                             f"{result.stdout.decode('utf-8', errors='replace')}\n"
                             f"{result.stderr.decode('utf-8', errors='replace')}")
    return result.stdout if binary else result.stdout.decode("utf-8")


def check_package(directory, scratch, harness):
    checks = 0

    def check(condition, description):
        nonlocal checks
        if not condition:
            raise AssertionError(description)
        checks += 1
        print(f"PASS {description}", flush=True)

    windows = os.name == "nt"
    rid = "win-x64" if windows else "linux-x64"
    executable = directory / ("advkit.exe" if windows else "advkit")
    check(executable.is_file(), "release contains the native CLI")
    spine_license = directory / "licenses" / "Spine-Runtimes.txt"
    spine_origin = directory / "licenses" / "Spine-Runtimes-Origin.md"
    check(spine_license.is_file() and spine_origin.is_file()
          and "Spine Runtimes License Agreement" in spine_license.read_text(encoding="utf-8")
          and "e7dc1435fa4a0083ab431f1b28e083c14a1f5c68" in spine_origin.read_text(encoding="utf-8"),
          "the compiled Spine runtime carries its separate license and pinned upstream origin")
    manifest = json.loads((directory / "native-dependencies.json").read_text(encoding="utf-8"))
    check(manifest["schema"] == "ournotes-native-dependencies/1" and manifest["runtimeIdentifier"] == rid,
          "native dependency manifest matches the target")
    for entry in manifest["files"]:
        path = (directory / entry["file"]).resolve(strict=True)
        check(path.is_relative_to(directory), f"dependency stays inside the package: {entry['file']}")
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        check(digest == entry["sha256"] and path.stat().st_size == entry["size"],
              f"dependency and notice checksum: {entry['file']}")
    expected_ids = {"cubism-core", "motionsync-cri"} if windows else {"cubism-core"}
    check({entry["id"] for entry in manifest["libraries"]} == expected_ids, "only supported target SDK runtimes are shipped")
    media_manifest = json.loads((directory / "media-dependencies.json").read_text(encoding="utf-8"))
    check(media_manifest["format"] == "ournotes.media-dependencies/1" and media_manifest["rid"] == rid,
          "media dependency manifest matches the target")
    for name, expected in media_manifest["files"].items():
        path = (directory / name).resolve(strict=True)
        check(path.is_relative_to(directory), f"media dependency stays inside the package: {name}")
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        check(digest == expected, f"media binary/source/notice checksum: {name}")
    check(len(list((directory / "media-sources").glob("*.tar.*"))) == 3
          and (directory / "media-sources" / "build-media.sh").is_file()
          and (directory / "media-sources" / "media-sources.lock").is_file(),
          "FFmpeg, x264 and zlib source archives and the exact rebuild scripts accompany the release")

    environment = os.environ.copy()
    for name in ("CUBISM_NATIVE_SDK", "CUBISM_MOTIONSYNC_DLL", "LD_LIBRARY_PATH"):
        environment.pop(name, None)
    environment["DOTNET_ROOT"] = str(scratch / "no-dotnet-runtime")
    environment["DOTNET_ROOT_X64"] = environment["DOTNET_ROOT"]
    environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"
    environment["OURNOTES_ARCHIVE"] = str(scratch / "no-game-archive")
    # Windows still needs its operating-system DLLs. Neither directory contains
    # a developer SDK nor a separately installed FFmpeg.
    system_root = Path(environment.get("SystemRoot", "C:/Windows"))
    environment["PATH"] = os.pathsep.join(map(str, (system_root / "System32", system_root))) if windows else "/nonexistent-ournotes-path"
    elsewhere = scratch / "unrelated working directory"
    elsewhere.mkdir()
    document = json.loads(run([executable, "probe", "runtime"], elsewhere, environment))
    check(document["ok"] is True, "CLI loads its runtime dependencies without an archive, SDK environment, runtime or FFmpeg PATH")
    runtime = document["result"]
    check(Path(runtime["applicationDirectory"]).samefile(directory), "dependency discovery follows the extracted executable")
    core = next(entry for entry in manifest["libraries"] if entry["id"] == "cubism-core")
    check(Path(runtime["core"]["path"]).samefile(directory / core["file"]), "the loaded Cubism Core is the packaged library")
    check(runtime["core"]["version"] == core["engineVersion"], "the actual Cubism Core version matches the lock")
    if windows:
        motion = next(entry for entry in manifest["libraries"] if entry["id"] == "motionsync-cri")
        check(Path(runtime["lipSync"]["path"]).samefile(directory / motion["file"]), "the loaded MotionSync engine is the packaged library")
        check(runtime["lipSync"]["version"] == motion["engineVersion"], "the actual MotionSync ABI matches the lock")
    else:
        check(runtime["lipSync"]["engine"] == "C# CRI Lips" and runtime["lipSync"]["nativeLibrary"] is False,
              "Linux retains the managed CRI Lips path")
    for tool in ("ffmpeg", "ffprobe"):
        expected = directory / "tools" / (tool + (".exe" if windows else ""))
        check(Path(runtime[tool]["path"]).samefile(expected), f"{tool} is resolved from the application tools directory")
        check(runtime[tool]["version"].startswith(tool + " version "), f"packaged {tool} executes successfully")
    check((directory / "licenses" / "ffmpeg").is_dir(), "FFmpeg notices accompany the executables")

    ffmpeg = Path(runtime["ffmpeg"]["path"])
    ffprobe = Path(runtime["ffprobe"]["path"])
    rgba = bytes(channel for y in range(64) for x in range(64)
                 for channel in (x * 4, y * 4, (x + y) * 2, (x * 17 + y * 13) % 256))
    raw = scratch / "input rgba.bin"
    raw.write_bytes(rgba * 6)
    wav = scratch / "input audio.wav"
    with wave.open(str(wav), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(48000)
        output.writeframes(b"".join(struct.pack("<h", round(12000 * math.sin(2 * math.pi * 440 * i / 48000)))
                                  for i in range(9600)))

    def ff(arguments, *, binary=False):
        return run([ffmpeg, "-v", "error", "-nostdin", "-y", *arguments], elsewhere, environment, binary=binary)

    raw_input = ["-f", "rawvideo", "-pix_fmt", "rgba", "-s", "64x64", "-r", "30", "-i", raw]
    video = scratch / "h264 and aac.mp4"
    ff([*raw_input, "-i", wav, "-t", "0.2", "-c:v", "libx264", "-pix_fmt", "yuv420p",
        "-threads", "1", "-c:a", "aac", video])
    ff(["-xerror", "-i", video, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"])
    samples = array.array("f", ff(["-i", video, "-map", "0:a:0", "-c:a", "pcm_f32le", "-f", "f32le", "-"], binary=True))
    check(len(samples) > 0 and max(map(abs, samples)) > .1, "packaged H.264/AAC encode and complete decode preserve an actual audio signal")

    lossless = scratch / "lossless alpha.mkv"
    ff([*raw_input, "-frames:v", "6", "-c:v", "ffv1", "-level", "3", "-pix_fmt", "bgra", "-threads", "1", lossless])
    decoded = ff(["-xerror", "-i", lossless, "-pix_fmt", "rgba", "-f", "rawvideo", "-"], binary=True)
    check(decoded == rgba * 6, "FFV1 RGBA round trip is byte-for-byte lossless, including alpha")

    prores = scratch / "prores 4444 alpha.mov"
    ff([*raw_input, "-frames:v", "1", "-c:v", "prores_ks", "-profile:v", "4", "-pix_fmt", "yuva444p10le",
        "-alpha_bits", "16", "-threads", "1", prores])
    decoded = ff(["-xerror", "-i", prores, "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", "-"], binary=True)
    check(len(decoded) == len(rgba) and max(abs(a - b) for a, b in zip(decoded[3::4], rgba[3::4])) <= 1,
          "ProRes 4444 retains transparent and partially transparent pixels")

    cineform = scratch / "cineform 12-bit alpha.mov"
    ff([*raw_input, "-frames:v", "6", "-vf", "format=rgba64le", "-c:v", "cfhd", "-pix_fmt", "gbrap12le", "-quality", "film1",
        "-tag:v", "CFHD", "-threads", "1", cineform])
    info = json.loads(run([ffprobe, "-v", "error", "-show_streams", "-of", "json", cineform], elsewhere, environment))
    stream = info["streams"][0]
    check(stream["codec_name"] == "cfhd" and stream["codec_tag_string"] == "CFHD"
          and stream["pix_fmt"] == "gbrap12le" and int(stream["nb_frames"]) == 6,
          "packaged CineForm creates a six-frame RGB 12-bit + alpha MOV with the native CFHD tag")
    decoded = ff(["-xerror", "-i", cineform, "-pix_fmt", "rgba", "-f", "rawvideo", "-"], binary=True)
    alpha_errors = [abs(a - b) for a, b in zip(decoded[3::4], (rgba * 6)[3::4])]
    check(len(decoded) == len(rgba) * 6 and max(alpha_errors) <= 8
          and sum(alpha_errors) / len(alpha_errors) <= 2,
          "CineForm film1 fully decodes with bounded alpha error, including transparent and fractional coverage")
    rgb_errors = [abs(a - b) for i, (a, b) in enumerate(zip(decoded, rgba * 6)) if i % 4 != 3]
    check(max(rgb_errors) <= 4 and sum(rgb_errors) / len(rgb_errors) <= 1,
          "CineForm keeps straight RGB values through semitransparent pixels without introducing a black matte")

    # Constant alpha planes catch a swscale conversion bias hidden by ordinary
    # lossy-error tolerances: direct RGBA8 -> GBRAP12 maps empty alpha to 8/4095.
    endpoint_pixels = b"".join(bytes((192, 64, 128, alpha)) * (64 * 64) for alpha in (0, 128, 255))
    endpoint_raw = scratch / "alpha endpoint rgba.bin"
    endpoint_raw.write_bytes(endpoint_pixels)
    endpoint_input = ["-f", "rawvideo", "-pix_fmt", "rgba", "-s", "64x64", "-r", "30", "-i", endpoint_raw]
    endpoint_lossless = scratch / "alpha endpoints.mkv"
    ff([*endpoint_input, "-c:v", "ffv1", "-level", "3", "-pix_fmt", "bgra", "-threads", "1", endpoint_lossless])

    def check_alpha_endpoints(decoded, description):
        frame_bytes = 64 * 64 * 4
        check(len(decoded) == frame_bytes * 3
              and set(decoded[3:frame_bytes:4]) == {0}
              and set(decoded[frame_bytes * 2 + 3::4]) == {255}
              and max(abs(alpha - 128) for alpha in decoded[frame_bytes + 3:frame_bytes * 2:4]) <= 2
              and max(abs(value - endpoint_pixels[i]) for i, value in enumerate(decoded) if i % 4 != 3) <= 2,
              description)

    for quality in ("film1", "high"):
        endpoint_movie = scratch / f"CineForm {quality} alpha endpoints.mov"
        ff([*endpoint_input, "-vf", "format=rgba64le", "-c:v", "cfhd", "-pix_fmt", "gbrap12le",
            "-quality", quality, "-tag:v", "CFHD", "-threads", "1", endpoint_movie])
        endpoint_decoded = ff(["-xerror", "-i", endpoint_movie, "-pix_fmt", "rgba", "-f", "rawvideo", "-"], binary=True)
        check_alpha_endpoints(endpoint_decoded, f"CineForm {quality} preserves empty/opaque alpha endpoints and straight semitransparent RGB")

    png = scratch / "static alpha.png"
    ff([*raw_input, "-frames:v", "1", "-c:v", "png", "-threads", "1", png])
    decoded = ff(["-xerror", "-i", png, "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", "-"], binary=True)
    check(decoded == rgba, "PNG encode/decode preserves exact RGBA pixels")

    filtered = array.array("f", ff(["-i", wav, "-af", "atempo=1.25,apad,atrim=duration=0.3", "-ar", "48000", "-ac", "2",
                                   "-c:a", "pcm_f32le", "-f", "f32le", "-"], binary=True))
    check(len(filtered) == 14400 * 2 and max(map(abs, filtered[:9600])) > .1 and max(map(abs, filtered[-2000:])) == 0,
          "atempo, apad and atrim produce the requested stereo duration with speech and trailing silence")

    # The existing media CLI shares the archive envelope; an empty local index
    # is sufficient, so no game content or network access is needed for this test.
    archive = scratch / "empty archive"
    (archive / "index").mkdir(parents=True)
    (archive / "index" / "stats.json").write_text("{}", encoding="utf-8")
    info = json.loads(run([executable, "media", "probe", video, "--archive", archive], elsewhere, environment))
    check(info["ok"] and {stream["codec_name"] for stream in info["result"]["streams"]} == {"h264", "aac"},
          "CLI media probe uses packaged ffprobe with an empty FFmpeg PATH")
    clip = scratch / "CLI ProRes clip.mov"
    info = json.loads(run([executable, "media", "clip", video, "--archive", archive, "--duration", "0.1", "-o", clip], elsewhere, environment))
    check(info["ok"] and clip.is_file(), "CLI media clip uses packaged FFmpeg and ffprobe")
    ff(["-xerror", "-i", clip, "-f", "null", "-"])
    probe = json.loads(run([ffprobe, "-v", "error", "-show_streams", "-of", "json", clip], elsewhere, environment))
    check(probe["streams"][0]["codec_name"] == "prores", "the CLI-produced ProRes clip completely decodes")
    for quality in ("film1", "high"):
        alpha_clip = scratch / f"CLI CineForm {quality} clip.mov"
        quality_options = [] if quality == "film1" else ["--cineform-quality", quality]
        info = json.loads(run([executable, "media", "clip", lossless, "--archive", archive, "--duration", "0.1",
                               "--codec", "cineform", *quality_options, "-o", alpha_clip], elsewhere, environment))
        check(info["ok"] and info["result"]["cineformQuality"] == quality and alpha_clip.is_file(),
              "CLI media clip uses CineForm's default film1 quality" if quality == "film1"
              else "CLI media clip accepts an explicit CineForm high quality")
        clip_rgba = ff(["-xerror", "-i", alpha_clip, "-pix_fmt", "rgba", "-f", "rawvideo", "-"], binary=True)
        check(len(clip_rgba) == len(rgba) * 3
              and max(abs(a - b) for a, b in zip(clip_rgba[3::4], (rgba * 3)[3::4])) <= 10,
              f"CLI CineForm {quality} trimming preserves all three frames and actual alpha")
        endpoint_clip = scratch / f"CLI CineForm {quality} alpha endpoints.mov"
        endpoint_info = json.loads(run([executable, "media", "clip", endpoint_lossless, "--archive", archive,
                                       "--duration", "0.1", "--codec", "cineform", *quality_options, "-o", endpoint_clip], elsewhere, environment))
        check(endpoint_info["ok"] and endpoint_clip.is_file(), f"CLI CineForm {quality} converts a lossless source with alpha endpoints")
        endpoint_decoded = ff(["-xerror", "-i", endpoint_clip, "-pix_fmt", "rgba", "-f", "rawvideo", "-"], binary=True)
        check_alpha_endpoints(endpoint_decoded, f"CLI CineForm {quality} preserves empty/opaque alpha and straight RGB through shared encoding")
    subtitles = scratch / "multilingual subtitles.srt"
    subtitle_text = "日本語の字幕・中文字幕"
    subtitles.write_text(f"1\n00:00:00,000 --> 00:00:00,350\n{subtitle_text}\n\n", encoding="utf-8")
    for extension, audio_codec, subtitle_codec in (("mp4", "aac", "mov_text"), ("mkv", "pcm_s16le", "subrip"), ("mov", "pcm_s24le", "mov_text")):
        muxed = scratch / ("CLI subtitles." + extension)
        source_video = cineform if extension == "mov" else video
        result = json.loads(run([executable, "media", "mux", source_video, "--archive", archive,
                                 "--audio", wav, "--subtitles", subtitles, "-o", muxed], elsewhere, environment))
        check(result["ok"] and muxed.is_file(), f"CLI media mux creates a subtitled {extension.upper()} using packaged tools")
        info = json.loads(run([ffprobe, "-v", "error", "-show_streams", "-of", "json", muxed], elsewhere, environment))
        streams = {stream["codec_type"]: stream for stream in info["streams"]}
        check(len(info["streams"]) == 3 and set(streams) == {"video", "audio", "subtitle"}
              and streams["video"]["codec_name"] == ("cfhd" if extension == "mov" else "h264")
              and streams["audio"]["codec_name"] == audio_codec
              and streams["audio"]["sample_rate"] == "48000" and streams["audio"]["channels"] == 2
              and streams["subtitle"]["codec_name"] == subtitle_codec,
              f"{extension.upper()} contains copied video, {audio_codec} stereo audio and a {subtitle_codec} subtitle track")
        ff(["-xerror", "-i", muxed, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"])
        decoded_subtitles = ff(["-xerror", "-i", muxed, "-map", "0:s:0", "-c:s", "srt", "-f", "srt", "-"])
        check(subtitle_text in decoded_subtitles and "00:00:00,350" not in decoded_subtitles,
              f"{extension.upper()} audio/video fully decode and multilingual subtitles round trip with duration clipping")
        if extension == "mov":
            original_pixels = ff(["-xerror", "-i", source_video, "-pix_fmt", "rgba", "-f", "rawvideo", "-"], binary=True)
            muxed_pixels = ff(["-xerror", "-i", muxed, "-pix_fmt", "rgba", "-f", "rawvideo", "-"], binary=True)
            check(streams["video"]["pix_fmt"] == "gbrap12le" and muxed_pixels == original_pixels,
                  "MOV audio/subtitle mux keeps CineForm RGB and alpha pixels exactly unchanged")
    if harness:
        print(run([harness, "--runtime-package", directory], elsewhere, environment), end="")
        check(True, "Native AOT harness loads the packaged SDK and exercises the supported lip-sync backend")
    print(f"Passed {checks} extracted-package checks for {rid}.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--package", type=Path, help="Final ZIP or tar.gz release")
    source.add_argument("--directory", type=Path, help="Already staged package directory")
    parser.add_argument("--native-tests", type=Path, help="Published Native AOT test harness")
    arguments = parser.parse_args()
    harness = arguments.native_tests.resolve(strict=True) if arguments.native_tests else None
    with tempfile.TemporaryDirectory(prefix="ournotes-package-") as temporary:
        scratch = Path(temporary).resolve()
        if arguments.package:
            package = arguments.package.resolve(strict=True)
            directory = scratch / "extracted application"
            directory.mkdir()
            if package.suffix.lower() == ".zip":
                with ZipFile(package) as archive:
                    for entry in archive.infolist():
                        if not (directory / entry.filename).resolve().is_relative_to(directory):
                            raise ValueError("Archive entry escapes the package directory")
                    archive.extractall(directory)
            else:
                with tarfile.open(package, "r:gz") as archive:
                    archive.extractall(directory, filter="data")
        else:
            directory = arguments.directory.resolve(strict=True)
        check_package(directory, scratch, harness)


if __name__ == "__main__":
    main()
