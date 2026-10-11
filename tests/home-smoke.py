#!/usr/bin/env python3
"""Exercise home discovery, batched CineForm exports and atomic failures through the real CLI.

The fixture writer is the existing NativeTests executable with --home-fixture support.
No game assets or Cubism SDK are needed. Use --managed for local DLL verification.
"""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--fixture-writer", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path)
    parser.add_argument("--ffprobe", type=Path)
    parser.add_argument("--managed", action="store_true", help="Run the CLI and fixture writer DLLs through dotnet")
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    binary = args.binary.resolve(strict=True)
    writer = args.fixture_writer.resolve(strict=True)
    prefix = [args.dotnet] if args.managed else []

    def media_tool(name, configured):
        if configured:
            return configured.resolve(strict=True)
        packaged = binary.parent / "tools" / (name + (".exe" if os.name == "nt" else ""))
        if packaged.is_file():
            return packaged
        found = shutil.which(name)
        if not found:
            raise FileNotFoundError(f"Pass --{name}, or place it in the CLI tools directory")
        return Path(found).resolve(strict=True)

    ffmpeg = media_tool("ffmpeg", args.ffmpeg)
    ffprobe = media_tool("ffprobe", args.ffprobe)
    checks = 0

    def check(condition, label):
        nonlocal checks
        if not condition:
            raise AssertionError(label)
        checks += 1
        print("PASS " + label, flush=True)

    if not args.managed:
        check(binary.read_bytes()[:4].startswith((b"\x7fELF", b"MZ")), "home smoke uses a published native CLI")

    with tempfile.TemporaryDirectory(prefix="ournotes-home-cli-") as temporary:
        scratch = Path(temporary).resolve()
        archive = scratch / "archive with spaces"
        working = scratch / "unrelated working directory"
        working.mkdir()
        environment = os.environ.copy()
        # A requested SDK path would fail if this route accidentally initialized Cubism.
        environment["CUBISM_NATIVE_SDK"] = str(scratch / "missing SDK")
        environment["CUBISM_MOTIONSYNC_DLL"] = str(scratch / "missing MotionSync.dll")
        environment["OURNOTES_ARCHIVE"] = str(scratch / "missing default archive")

        def run(arguments, *, success=True, raw=False):
            result = subprocess.run([str(value) for value in arguments], cwd=working, env=environment,
                                    capture_output=True, timeout=90)
            if (result.returncode == 0) != success:
                raise AssertionError(f"Unexpected exit {result.returncode}: {arguments}\n"
                                     f"{result.stdout.decode('utf-8', errors='replace')}\n"
                                     f"{result.stderr.decode('utf-8', errors='replace')}")
            return result.stdout if raw else result.stdout.decode("utf-8")

        run([*prefix, writer, "--home-fixture", archive])
        # Reuse the native rendering fixture, adding one independently placed instance of
        # the same skeleton so batch iteration and per-character selection cover two people.
        manifest_path = archive / "sites" / "intl" / "stories" / "42.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        spot_entry = manifest["files"]["host/spot/spot.json"]
        spot = json.loads((archive / "sites" / "intl" / spot_entry["asset"]).read_bytes())
        spot["characters"].append({"path": "home/Bob", "_characterId": 2})
        second = copy.deepcopy(spot["spineCharacters"][0])
        second["path"] = "home/Bob"
        second["world"][13] = 0.8
        spot["spineCharacters"].append(second)
        def save_spot():
            encoded = json.dumps(spot, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            asset = "assets/" + hashlib.sha256(encoded).hexdigest() + ".json"
            (archive / "sites" / "intl" / asset).write_bytes(encoded)
            manifest["files"]["host/spot/spot.json"] = {"asset": asset, "size": len(encoded)}
            manifest_path.write_text(json.dumps(manifest, separators=(",", ":")), encoding="utf-8")

        save_spot()

        def cli(arguments, *, success=True):
            document = json.loads(run([*prefix, binary, "home", *arguments, "--archive", archive], success=success))
            check(document["ok"] is success, "CLI returns a consistent JSON success/error envelope")
            return document["result"] if success else document["error"]

        def probe(path):
            return json.loads(run([ffprobe, "-v", "error", "-count_frames", "-show_streams", "-of", "json", path]))["streams"][0]

        def decode(path):
            return run([ffmpeg, "-v", "error", "-nostdin", "-xerror", "-i", path,
                        "-map", "0:v:0", "-pix_fmt", "rgba", "-f", "rawvideo", "-"], raw=True)

        def dimensions(result, frames):
            return result["width"] == 128 and result["height"] == 96 and result["frameCount"] == frames

        def clean_failure(arguments, output, label, *, existing=None):
            cli(arguments, success=False)
            check((output.read_bytes() == existing) if existing is not None else not output.exists(), label)
            check(not list(output.parent.glob(output.name + ".*.tmp")), "failed home command removes its staging files and directories")

        listed = cli(["list"])
        shown = cli(["show", "77"])
        check(listed["total"] == 1 and listed["items"][0]["spotId"] == 77
              and {c["key"] for c in shown["characters"]} == {"alice", "bob"},
              "old archive discovery and home show expose both characters without an SDK")
        animations = cli(["animations", "77"])
        check(animations["sourceDuration"] == 1 and len(animations["parts"]) == 2
              and all(p["animation"] == "home_start" and p["loop"] is False for p in animations["parts"]),
              "home animations reports the source duration, selected tracks and non-looping behavior")

        render_options = ["--width", "128", "--height", "96", "--supersample", "2", "--fps", "30"]
        video_options = [*render_options, "--ffmpeg", ffmpeg]
        batch = scratch / "complete batch"
        report = cli(["export", "77", *video_options, "--out", batch])
        expected = {"scene.mov", "characters/alice.mov", "characters/bob.mov"}
        check(report["fileCount"] == 3 and report["cineformQuality"] == "film1"
              and {e["output"] for e in report["exports"]} == expected
              and json.loads((batch / "export.json").read_text(encoding="utf-8")) == report,
              "one batch publishes the complete scene, every character and a matching export manifest")
        frame_bytes = 128 * 96 * 4
        decoded = {}
        for item in report["exports"]:
            path = batch / item["output"]
            stream = probe(path)
            check(dimensions(item, 31) and item["includesSourceEnd"] is True
                  and stream["codec_name"] == "cfhd" and stream["codec_tag_string"] == "CFHD"
                  and stream["pix_fmt"] == "gbrap12le" and int(stream["nb_read_frames"]) == 31,
                  f"{item['output']} is a 31-frame CineForm RGB 12-bit + alpha MOV including the source endpoint")
            pixels = decode(path)
            check(len(pixels) == frame_bytes * 31, f"every frame completely decodes in {item['output']}")
            decoded[item["output"]] = pixels
        scene_pixels = decoded["scene.mov"]
        check(min(scene_pixels[3::4]) >= 250 and scene_pixels[1] >= 245
              and scene_pixels[0] <= 10 and scene_pixels[2] <= 10,
              "the complete scene retains its opaque green room")
        character_pixels = decoded["characters/alice.mov"]
        alphas = character_pixels[3::4]
        check(min(alphas) <= 2 and max(alphas) >= 250 and any(16 < value < 240 for value in alphas),
              "the character CineForm contains transparent, opaque and fractional alpha rather than a matte")
        opaque_indices = [offset for offset in range(0, len(character_pixels), 4) if character_pixels[offset + 3] >= 245]
        check(opaque_indices and all(character_pixels[offset] <= 8 and character_pixels[offset + 1] <= 8
                                     and character_pixels[offset + 2] >= 245 for offset in opaque_indices),
              "individual character output preserves its blue artwork and excludes the room")
        first = character_pixels[:frame_bytes]
        last = character_pixels[-frame_bytes:]

        def center_x(pixels):
            positions = [((offset // 4) % 128, pixels[offset + 3]) for offset in range(0, len(pixels), 4)
                         if pixels[offset + 3] > 128]
            return sum(x * alpha for x, alpha in positions) / sum(alpha for _, alpha in positions)

        check(center_x(last) - center_x(first) > 40, "stable character framing retains the motion from the true start through the final pose")

        all_characters = scratch / "all archived characters"
        selected = cli(["export", "all", *video_options, "--character", "all", "--duration", "0.1", "--out", all_characters])
        check(selected["spotCount"] == 1 and selected["fileCount"] == 2
              and {e["output"] for e in selected["exports"]} == {"intl-77/characters/alice.mov", "intl-77/characters/bob.mov"}
              and all(e["frameCount"] == 3 for e in selected["exports"]),
              "export all with character all traverses the archive and publishes separate character files")

        # The PNG sequence and clip use the same explicitly trimmed interval and crop.
        interval = ["--character", "alice", "--time", "0.2", "--duration", "0.2"]
        sequence = scratch / "trimmed sequence"
        sequence_report = cli(["sequence", "77", *render_options, *interval, "--out", sequence])
        clip = scratch / "trimmed clip.mov"
        clip_report = cli(["clip", "77", *video_options, *interval, "--out", clip])
        pngs = sorted(sequence.glob("frame-*.png"))
        check(dimensions(sequence_report, 6) and dimensions(clip_report, 6)
              and sequence_report["firstFrame"] == clip_report["firstFrame"] == 6
              and len(pngs) == 6 and sequence_report["cropMatrix"] == clip_report["cropMatrix"],
              "clip and sequence trim the same six frames without changing their crop")
        reference = b"".join(decode(path) for path in pngs)
        trimmed = decode(clip)
        errors = [abs(a - b) for a, b in zip(reference[3::4], trimmed[3::4])]
        check(len(trimmed) == len(reference) == frame_bytes * 6 and max(errors) <= 10
              and sum(errors) / len(errors) <= 1,
              "CineForm alpha matches every lossless PNG frame within bounded codec error")

        before = {str(p.relative_to(batch)): hashlib.sha256(p.read_bytes()).hexdigest() for p in batch.rglob("*") if p.is_file()}
        cli(["export", "77", *video_options, "--out", batch], success=False)
        after = {str(p.relative_to(batch)): hashlib.sha256(p.read_bytes()).hexdigest() for p in batch.rglob("*") if p.is_file()}
        check(before == after, "batch export refuses an existing directory without changing any published file")
        cli(["sequence", "77", *render_options, *interval, "--out", sequence, "--overwrite"], success=False)
        check(len(list(sequence.glob("frame-*.png"))) == 6, "sequence refuses directory overwrite and retains its complete prior output")
        bad_size = scratch / "bad-size.mov"
        clean_failure(["clip", "77", *video_options, "--width", "129", "--out", bad_size], bad_size,
                      "unsupported CineForm dimensions fail before publishing a file")
        bad_alpha = scratch / "alpha.mp4"
        clean_failure(["clip", "77", *video_options, "--character", "alice", "--codec", "h264", "--out", bad_alpha], bad_alpha,
                      "H264 cannot silently remove the character alpha channel")
        bad_animation = scratch / "bad-animation-batch"
        clean_failure(["export", "77", *video_options, "--animation", "does-not-exist", "--out", bad_animation], bad_animation,
                      "a missing animation leaves no partial batch directory")
        protected = scratch / "protected.mov"
        sentinel = b"existing user output must remain unchanged"
        protected.write_bytes(sentinel)
        # ffprobe is an executable but not an encoder: fail after rendering starts, not at path validation.
        clean_failure(["clip", "77", *render_options, "--ffmpeg", ffprobe, *interval,
                       "--out", protected, "--overwrite"], protected,
                      "an encoder failure preserves the original file even with --overwrite", existing=sentinel)
        # The complete scene and Alice can render, but Bob cannot be fitted from behind the
        # camera. This fails after earlier videos in the batch have already been encoded.
        second["world"][14] = -10
        save_spot()
        partial_batch = scratch / "partially rendered batch"
        clean_failure(["export", "77", *video_options, "--duration", "0.1", "--out", partial_batch], partial_batch,
                      "a later character failure rolls back earlier encoded videos in the same batch")
        check(not list(scratch.glob("*.tmp")), "successful and failed home commands leave no staging directories")

    print(json.dumps({"ok": True, "checks": checks, "binary": str(binary), "nativeAot": not args.managed}))


if __name__ == "__main__":
    main()
