#!/usr/bin/env python3
"""Stage the exact Cubism runtimes and their notices into an application package."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
import time
from urllib.request import Request, urlopen
from zipfile import ZipFile


REPOSITORY = Path(__file__).resolve().parent.parent
LOCK = REPOSITORY / "scripts" / "native-sources.lock.json"


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify(path, expected):
    actual = digest(path)
    if actual != expected:
        raise ValueError(f"SHA-256 mismatch for {path}: expected {expected}, got {actual}")


def obtain(source, cache):
    if "file" in source:
        path = REPOSITORY / source["file"]
        verify(path, source["sha256"])
        return path
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / (source["sha256"] + ".zip")
    if path.exists():
        verify(path, source["sha256"])
        return path
    print(f"Downloading pinned SDK archive: {source['url']}", flush=True)
    for attempt in range(3):
        descriptor, temporary = tempfile.mkstemp(prefix="native-sdk-", suffix=".part", dir=cache)
        temporary = Path(temporary)
        try:
            with os.fdopen(descriptor, "wb") as output:
                request = Request(source["url"], headers={"User-Agent": "ournotes-director-build/1"})
                with urlopen(request, timeout=60) as response:
                    shutil.copyfileobj(response, output)
            if temporary.stat().st_size != source["size"]:
                raise ValueError(f"SDK archive size mismatch: {source['url']}")
            verify(temporary, source["sha256"])
            temporary.replace(path)
            return path
        except Exception:
            temporary.unlink(missing_ok=True)
            if attempt == 2:
                raise
            time.sleep(2 ** attempt)
    raise AssertionError("Download attempts exhausted")


def extract_entry(archive, entry, destination):
    payload = archive.read(entry["entry"])
    if len(payload) != entry["size"] or hashlib.sha256(payload).hexdigest() != entry["sha256"]:
        raise ValueError(f"Pinned SDK entry changed: {entry['entry']}")
    path = destination / entry["file"]
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return {key: entry[key] for key in ("file", "size", "sha256")}


def stage(rid, output, cache):
    lock = json.loads(LOCK.read_text(encoding="utf-8"))
    if lock["schema"] != "ournotes-native-sources/1":
        raise ValueError("Unknown native dependency lock format")
    output.mkdir(parents=True, exist_ok=True)
    manifest = {"schema": "ournotes-native-dependencies/1", "runtimeIdentifier": rid, "libraries": [], "files": []}
    for component in lock["components"]:
        if rid not in component["runtimes"]:
            continue
        source = component["source"]
        archive_path = obtain(source, cache)
        runtime = component["runtimes"][rid]
        with ZipFile(archive_path) as archive:
            for entry in [runtime, *component["notices"]]:
                manifest["files"].append(extract_entry(archive, entry, output))
        manifest["libraries"].append({
            **{key: component[key] for key in ("id", "name", "version", "engineVersion")},
            **{key: runtime[key] for key in ("file", "sha256", "size", "systemDependencies")},
            "source": source,
            "notices": [entry["file"] for entry in component["notices"]],
        })
    if rid == "linux-x64":
        manifest["lipSync"] = {"engine": "C# CRI Lips", "weights": "From the selected game archive", "motionSyncNativeAvailable": False}
    (output / "native-dependencies.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Staged {len(manifest['libraries'])} verified native libraries and their notices for {rid}: {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=("win-x64", "linux-x64"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cache", type=Path, default=REPOSITORY / "artifacts" / "dependency-cache")
    arguments = parser.parse_args()
    stage(arguments.rid, arguments.output.resolve(), arguments.cache.resolve())
