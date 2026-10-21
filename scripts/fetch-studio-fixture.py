#!/usr/bin/env python3
"""Fetch a real ADV regression archive (10462 by default); preserve source manifests.

Only test setup needs Python/network access. The released editor/player does not.
The downloaded asset names contain the SHA-256 of the decoded bytes. This fixture
records the current upstream manifest hashes instead of assuming an immutable CDN.
"""
import argparse
import concurrent.futures
import gzip
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import time
import urllib.request


BASE = "https://storage.bdon.moe/moenotes/"
ADV = 10462
LANGUAGE = "zh-Hans"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path, help="New or previously downloaded test archive directory")
    parser.add_argument("--adv", type=int, default=ADV, help="Original story ID (default: 10462)")
    parser.add_argument("--model", action="append", default=[], help="Additional original model ID; repeat to add models")
    args = parser.parse_args()
    adv = args.adv
    if adv <= 0 or any(not re.fullmatch(r"[A-Za-z0-9_-]+", model) for model in args.model):
        parser.error("Use a positive story ID and plain original model IDs.")
    root = args.archive.resolve()
    site = root / "sites" / "intl"
    marker = root / "studio-fixture.json"
    if root.exists() and any(root.iterdir()) and not marker.exists():
        raise ValueError("Use an empty fixture directory; this script must not rewrite an existing user archive.")
    root.mkdir(parents=True, exist_ok=True)
    if not marker.exists():
        marker.write_text(json.dumps({"format": "ournotes-director/studio-fixture", "adv": adv}), encoding="utf-8")
    previous = json.loads(marker.read_text(encoding="utf-8"))
    if previous.get("format") != "ournotes-director/studio-fixture" or previous.get("adv") != adv:
        raise ValueError("This directory belongs to another fixture.")

    def destination(relative):
        path = PurePosixPath(relative)
        if path.is_absolute() or ".." in path.parts or "\\" in relative or ":" in relative:
            raise ValueError("Unsafe manifest path: " + relative)
        return site.joinpath(*path.parts)

    def fetch(relative):
        request = urllib.request.Request(BASE + relative, headers={"User-Agent": "ournotes-director-studio-regression"})
        for attempt in range(3):
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    return response.read()
            except Exception:
                if attempt == 2:
                    raise
                time.sleep(attempt + 1)

    def decode(relative, data):
        if relative.endswith(".gz"):
            data = gzip.decompress(data)
        elif relative.endswith(".br"):
            # Current fixture assets use gzip, JSON, PNG and M4A. Explicitly report a
            # future archive compression change rather than keeping corrupt data.
            try:
                import brotli
            except ImportError as error:
                raise RuntimeError("Upstream now uses Brotli; install Python brotli for test setup.") from error
            data = brotli.decompress(data)
        if relative.startswith("assets/"):
            expected = PurePosixPath(relative).name.split(".", 1)[0]
            if hashlib.sha256(data).hexdigest() != expected:
                raise ValueError("SHA-256 mismatch: " + relative)
        return data

    def obtain(relative, size=None):
        path = destination(relative)
        if path.exists():
            try:
                result = decode(relative, path.read_bytes())
                if size is None or len(result) == size:
                    return result
            except (ValueError, OSError, EOFError):
                pass
        encoded = fetch(relative)
        result = decode(relative, encoded)
        if size is not None and len(result) != size:
            raise ValueError("Decoded size mismatch: " + relative)
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_name(path.name + ".part")
        temporary.write_bytes(encoded)
        os.replace(temporary, path)
        return result

    def logical(entry):
        if "asset" in entry:
            return obtain(entry["asset"], entry.get("size"))
        # The original numeric tokens in JSON parts stay untouched.
        return b"{" + b",".join(json.dumps(part[0]).encode("utf-8") + b":" + obtain(part[1], part[2])
                                 for part in entry["parts"]) + b"}"

    manifests = {f"stories/{adv}.json": obtain(f"stories/{adv}.json")}
    story_manifest = json.loads(manifests[f"stories/{adv}.json"])
    definition = json.loads(logical(story_manifest["files"]["story.json"]))
    for model in sorted(set(definition.get("models", {}).values()) | set(args.model)):
        relative = "models/" + model + ".json"
        manifests[relative] = obtain(relative)
    assets = {}
    for data in manifests.values():
        document = json.loads(data)
        files = {**document["files"], **document.get("languages", {}).get(LANGUAGE, {}).get("files", {})}
        for entry in files.values():
            if "asset" in entry:
                assets[entry["asset"]] = entry.get("size")
            else:
                for part in entry["parts"]:
                    assets[part[1]] = part[2]
    print(f"Fetching real ADV {adv}: {len(manifests) - 1} models and {len(assets)} verified assets.", flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        decoded_bytes = sum(pool.map(lambda pair: len(obtain(*pair)), assets.items()))

    # Store the original discovery response unchanged. The selected fixture index is
    # derived separately; all story/model manifests above remain byte-for-byte intact.
    discovery = json.loads(obtain("stories.json"))
    selected = [row for row in discovery["stories"] if str(row.get("advId")) == str(adv)]
    if len(selected) != 1:
        raise ValueError("The requested original ADV is absent or ambiguous in discovery.")
    index = root / "index"
    index.mkdir(exist_ok=True)
    selected_story = {**selected[0], "site": "intl", "languages": [LANGUAGE]}
    selected_models = []
    for relative, data in manifests.items():
        if not relative.startswith("models/"):
            continue
        document = json.loads(data)
        selected_models.append({"site": "intl", "id": document["id"], "manifest": relative,
                                "key": document["key"], **document["model"], "logicalFiles": list(document["files"])})
    (index / "stats.json").write_text(json.dumps({"stories": 1, "models": len(selected_models)}) + "\n", encoding="utf-8")
    (index / "stories.jsonl").write_text(json.dumps(selected_story, ensure_ascii=False) + "\n", encoding="utf-8")
    # The ordinary source/model pickers can use this small fixture too. These are
    # derived indexes, not edits to the stored upstream story/model manifests.
    (index / "catalog.json").write_text(json.dumps({"format": "millsage.catalog/1", "stories": [selected_story]}, ensure_ascii=False) + "\n", encoding="utf-8")
    (index / "models.json").write_text(json.dumps({"format": "millsage.models/1", "models": selected_models}, ensure_ascii=False) + "\n", encoding="utf-8")
    project = {"format": "ournotes-director/project", "version": 1,
               "source": {"adv": str(adv), "site": "intl", "language": LANGUAGE}, "output": {}}
    (root / "original.project.json").write_text(json.dumps(project, ensure_ascii=False) + "\n", encoding="utf-8")
    report = {"format": "ournotes-director/studio-fixture", "adv": adv, "site": "intl", "language": LANGUAGE,
              "additionalModels": sorted(set(args.model)),
              "baseUrl": BASE, "assetCount": len(assets), "decodedBytes": decoded_bytes,
              "manifestSha256": {name: hashlib.sha256(data).hexdigest() for name, data in manifests.items()}}
    marker.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Verified {decoded_bytes:,} decoded bytes; archive: {root}", flush=True)


if __name__ == "__main__":
    main()
