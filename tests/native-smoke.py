#!/usr/bin/env python3
"""Exercise the published executable without relying on the installed SDK/runtime."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile


parser = argparse.ArgumentParser()
parser.add_argument("--binary", type=Path, required=True)
args = parser.parse_args()
binary = args.binary.resolve(strict=True)
payload = {entry.name.lower() for entry in binary.parent.iterdir() if entry.is_file()}
assert not any(name.endswith((".runtimeconfig.json", ".deps.json")) for name in payload), payload
assert not any(name in payload for name in ("advkit.dll", "libcoreclr.so", "coreclr.dll", "hostfxr.dll", "libhostfxr.so")), payload
with binary.open("rb") as stream:
    magic = stream.read(4)
assert magic.startswith((b"\x7fELF", b"MZ")), "Published output is not a native PE/ELF executable"

with tempfile.TemporaryDirectory(prefix="ournotes-native-") as scratch:
    environment = os.environ.copy()
    environment["DOTNET_ROOT"] = str(Path(scratch) / "no-runtime")
    environment["DOTNET_ROOT_X64"] = environment["DOTNET_ROOT"]
    environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"
    environment["OURNOTES_ARCHIVE"] = str(Path(scratch) / "no-archive")
    result = subprocess.run([str(binary), "help"], cwd=scratch, env=environment, text=True,
                            encoding="utf-8", capture_output=True, timeout=20, check=True)
    help_document = json.loads(result.stdout)
    assert help_document["schema"] == "ournotes-director/1", help_document
    assert any(command.startswith("archive ") for command in help_document["commands"]), help_document
    result = subprocess.run([str(binary), "probe", "archive"], cwd=scratch, env=environment,
                            text=True, encoding="utf-8", capture_output=True, timeout=20)
    assert result.returncode == 3, result
    error = json.loads(result.stdout)
    assert error["ok"] is False and error["exitCode"] == 3, error

print("Native executable: PE/ELF, no managed host/runtime payload, help and error JSON passed.")
