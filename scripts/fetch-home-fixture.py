#!/usr/bin/env python3
"""Fetch a public home regression fixture (40002 by default), preserving source manifests.

Usage: python scripts/fetch-home-fixture.py <new-fixture-directory>
Only this test setup uses Python/network access. The native application does not.
Game artwork is downloaded into the chosen fixture directory, never into source.
"""
import argparse
import concurrent.futures
import gzip
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import time
import urllib.request


BASE = "https://storage.bdon.moe/moenotes/"
SPOT = 40002
SOURCES = (10855, 10853)  # Area and Hotaru tap sources of the same complete home.


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--spot", type=int, default=SPOT, help="Original home spot ID (default: 40002)")
    parser.add_argument("--sources", type=int, nargs="+", default=list(SOURCES), help="Area source followed by optional tap source IDs")
    args = parser.parse_args()
    spot = args.spot
    sources = tuple(dict.fromkeys(args.sources))
    if spot <= 0 or not sources or any(adv <= 0 for adv in sources):
        parser.error("Use positive original spot and story IDs.")
    root = args.archive.resolve()
    marker = root / "home-fixture.json"
    if root.exists() and any(root.iterdir()) and not marker.exists():
        raise ValueError("Use an empty fixture directory; an existing user archive must not be rewritten.")
    root.mkdir(parents=True, exist_ok=True)
    identity = {"format": "ournotes-director/home-fixture", "spot": spot, "sources": list(sources)}
    if marker.exists():
        old = json.loads(marker.read_text(encoding="utf-8"))
        if any(old.get(key) != value for key, value in identity.items()):
            raise ValueError("This directory belongs to a different fixture.")
    else:
        marker.write_text(json.dumps(identity), encoding="utf-8")
    site = root / "sites" / "intl"

    def destination(relative):
        path = PurePosixPath(relative)
        if path.is_absolute() or ".." in path.parts or "\\" in relative or ":" in relative:
            raise ValueError("Unsafe manifest path: " + relative)
        return site.joinpath(*path.parts)

    def fetch(relative):
        request = urllib.request.Request(BASE + relative, headers={"User-Agent": "ournotes-director-home-regression"})
        for attempt in range(3):
            try:
                with urllib.request.urlopen(request, timeout=45) as response:
                    return response.read()
            except Exception:
                if attempt == 2:
                    raise
                time.sleep(attempt + 1)

    def write(path, data):
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_name(path.name + ".partial")
        temporary.write_bytes(data)
        temporary.replace(path)

    manifests = {}
    fingerprints = {}
    for adv in sources:
        relative = f"stories/{adv}.json"
        path = destination(relative)
        raw = path.read_bytes() if path.exists() else fetch(relative)
        document = json.loads(raw)
        if document.get("advId") != adv or document.get("host", {}).get("kind") != "home":
            raise ValueError("Unexpected home source: " + relative)
        groups = document.get("story", {}).get("groups", [])
        if not any(group.get("kind") == "spot" and group.get("id") == spot for group in groups):
            raise ValueError("Home source does not belong to the requested spot: " + relative)
        manifests[adv] = document
        fingerprints[relative] = hashlib.sha256(raw).hexdigest()
        write(path, raw)  # Preserve every source number token and the original file table.

    # These regression homes have no active global post stack. Their host shader
    # index contains room/Spine shaders; unrelated ADV media and models are omitted.
    selected = {key: entry for key, entry in manifests[sources[0]]["files"].items()
                if key.startswith("host/") or key in ("scene.json", "settings.json", "story.json")}
    assets = {}

    def include(entry):
        parts = [(entry.get("asset"), entry.get("size"), entry.get("stored"))] if "asset" in entry else [
            (part[1], part[2], part[3] if len(part) > 3 else None) for part in entry["parts"]]
        for relative, size, stored in parts:
            if not re.fullmatch(r"assets/[0-9a-f]{64}\.[a-z0-9]+(?:\.(?:gz|br))?", relative or ""):
                raise ValueError("Invalid hashed asset path: " + str(relative))
            if relative in assets and assets[relative] != (size, stored):
                raise ValueError("Conflicting source sizes: " + relative)
            assets[relative] = (size, stored)

    for entry in selected.values():
        include(entry)
    for manifest in manifests.values():
        include(manifest["files"]["host/host.json"])

    def validate(relative, raw, size, stored):
        if stored is not None and len(raw) != stored:
            raise ValueError("Stored size mismatch: " + relative)
        data = gzip.decompress(raw) if relative.endswith(".gz") else raw
        if relative.endswith(".br"):
            try:
                import brotli
            except ImportError as error:
                raise RuntimeError("Upstream now uses Brotli; install Python brotli for fixture setup.") from error
            data = brotli.decompress(raw)
        if size is not None and len(data) != size:
            raise ValueError("Decoded size mismatch: " + relative)
        if hashlib.sha256(data).hexdigest() != PurePosixPath(relative).name.split(".", 1)[0]:
            raise ValueError("SHA-256 mismatch: " + relative)
        return len(raw)

    def obtain(item):
        relative, (size, stored) = item
        path = destination(relative)
        if path.exists():
            try:
                return validate(relative, path.read_bytes(), size, stored)
            except (ValueError, OSError):
                pass
        raw = fetch(relative)
        count = validate(relative, raw, size, stored)
        write(path, raw)
        return count

    print(f"Checking {len(selected)} home files, {len(assets)} distinct hashed assets...", flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        stored_bytes = sum(pool.map(obtain, sorted(assets.items())))
    # A small local discovery index may be generated; the original manifests and
    # all archive content remain byte-identical to the published source.
    entries = [{"advId": adv, "manifest": f"stories/{adv}.json", "groups": []} for adv in sources]
    write(site / "stories.json", json.dumps({"stories": entries}).encode())
    write(root / "index" / "stories.jsonl", ("\n".join(json.dumps({**entry, "site": "intl"}) for entry in entries) + "\n").encode())
    write(root / "index" / "stats.json", b"{}")
    report = {**identity, "publicBase": BASE, "manifestSha256": fingerprints,
              "logicalFiles": len(selected), "assets": len(assets), "storedBytes": stored_bytes,
              "note": "Original component references and world transforms are preserved without rewriting."}
    write(marker, (json.dumps(report, indent=2) + "\n").encode())
    print(f"Ready: {root} ({stored_bytes} stored bytes; all decoded SHA-256 values verified)", flush=True)


if __name__ == "__main__":
    main()
