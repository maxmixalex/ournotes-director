#!/usr/bin/env python3
"""Exercise the shipped CLI against a deterministic HTTP origin; no Node or NuGet tests.

The server tracks actual asset requests, so incremental behavior is verified rather
than inferred from an exit code. All fixtures and archives stay in a temporary folder.
"""

from __future__ import annotations

import argparse
from collections import Counter
from contextlib import contextmanager
import gzip
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
from urllib.parse import urlsplit


def encode(value):
    if isinstance(value, bytes):
        return value
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")


def texts(value):
    return {key: value for key in ("japanese", "english", "traditionalChinese", "simplifiedChinese", "korean")}


class Origin:
    def __init__(self):
        self.files = {}
        self.requests = Counter()
        self.responses = Counter()
        self.fail_once = set()
        self.gate = threading.Lock()

    def put(self, path, value):
        self.files[path] = encode(value)

    def asset(self, value, extension="json", compression=None):
        decoded = encode(value)
        suffix = f".{compression}" if compression else ""
        relative = f"assets/{hashlib.sha256(decoded).hexdigest()}.{extension}{suffix}"
        if compression == "gz":
            stored = gzip.compress(decoded, mtime=0)
        elif compression == "br":
            # An uncompressed Brotli stream, generated once from these exact bytes.
            assert decoded == b'{"brotli":true,"number":1e999}'
            stored = bytes.fromhex("8b0e807b2262726f746c69223a747275652c226e756d626572223a31653939397d03")
        else:
            stored = decoded
        for site in ("intl", "jp"):
            self.put(f"/{site}/{relative}", stored)
        return {"asset": relative, "size": len(decoded), "stored": len(stored)}

    def asset_requests(self):
        with self.gate:
            return Counter({path: count for path, count in self.requests.items() if "/assets/" in path})

    def fixture(self):
        self.put("/metadata/current_version.json", {"regions": {"hk-tw-mo": {"version": "v1"}, "jp": {"version": "v1"}}})
        self.tables = {
            "MasterBand.json": [{"_id": 4, "_nameTextID": "Band_Name_millsage"}],
            "MasterCharacter.json": [{"_id": 16, "_bandID": 4, "_nameTextID": "Character_Name_hotaru"}],
            "MasterStoryChapter.json": [{"_id": 4, "_bandId": 4, "_mainCharacterIds": [16]}],
            "MasterStoryEpisode.json": [{"_id": 1, "_advId": 7, "_chapterId": 4, "_episodeNumber": 1}],
            "MasterStoryFriendshipEpisode.json": [],
            "MasterCharacterFriendship.json": [],
            "MasterStoryLiveResultEpisode.json": [],
            "MasterStoryHomeSpotTapTalkEpisode.json": [],
            "MasterHomeSpot.json": [],
            "MasterAdv.json": [{"_id": 7, "_advEpisodeAsset": "adv_7", "_playbackMode": 0}],
        }
        self.publish_tables()
        self.wave = self.asset(b"original-voice-data", "m4a")
        self.raw = self.asset({"raw": True})
        self.brotli = self.asset(b'{"brotli":true,"number":1e999}', compression="br")
        part_number = self.asset(b"1e999")
        part_array = self.asset([1, 2, 3], compression="gz")
        self.split = {"parts": [[name, item["asset"], item["size"], item["stored"]] for name, item in (("number", part_number), ("items", part_array))]}
        self.chinese = self.asset({"language": "zh-Hans", "text": "归档测试"})
        self.model_id = "adv_live2d_hotaru_016_fixture"
        self.model_manifest = {
            "format": "ournotes.model-manifest/1",
            "model": {"id": self.model_id, "character": 16, "label": "fixture"},
            "files": {
                "model.json": self.asset({"prefab": "prefabs/model.json"}),
                "prefabs/model.json": self.asset({"canvas": {"width": 2, "height": 2}, "nodes": [{"components": [
                    {"class": "Live2DCharacter", "DefaultMotionName": "idle", "_motionList": [{"clip": "idle"}]},
                    {"class": "CubismExpressionController", "ExpressionsList": {"CubismExpressionObjects": [{"name": "smile.exp3"}]}},
                ]}]}),
                "model.moc3": self.asset(b"fake-model-for-archive-testing", "moc3"),
            },
        }
        self.unselected = self.asset(b"must-not-download", "moc3")
        for site in ("intl", "jp"):
            self.put(f"/{site}/models/{self.model_id}.json", self.model_manifest)
            self.put(f"/{site}/models/unrelated.json", {"model": {"character": 99}, "files": {"unused.moc3": self.unselected}})
        self.manifests = {number: self.story(number) for number in (7, 8, 9)}
        self.manifests[9]["models"] = {"unrelated": "models/unrelated.json"}
        self.publish_stories()
        self.fail_once.add("/intl/" + self.raw["asset"])

    def publish_tables(self):
        for region in ("tw", "jp"):
            for name, rows in self.tables.items():
                self.put(f"/metadata/{region}/master/{name}", rows)

    def story(self, number):
        episode = {
            "advId": number, "asset": f"adv_{number}", "commandCount": 2,
            "commands": [
                {"i": 1, "cmd": "Talk", "raw": 2, "TargetName": "hotaru", "AdvTextID": "t1", "VoiceIDs": [2], "voiceCues": ["voice_a"], "MotionName": "idle", "ExpressionName": "smile", "lines": texts("hello archive")},
                {"i": 2, "cmd": "Zoom", "raw": 11, "Parameter1": 1.2, "IsNoWait": 1},
            ],
            "text": {"t1": texts("hello archive")},
            "sounds": {"2": {"_soundCueSheetID": 5, "_cueName": "voice_a"}},
            "cuesheets": {"5": {"_cueSheetName": "sheet_a"}},
            "videos": {}, "resources": [],
            "master": {"_id": number, "_advEpisodeAsset": f"adv_{number}", "_playbackMode": 0},
            "title": texts(f"Fixture {number}"),
        }
        story = {"advId": number, "episode": "episode.json", "scene": "scene.json", "models": {"Character/Live2D/hotaru": self.model_id}, "audio": {"sheet_a": "audio/sheet_a"}}
        return {
            "format": "ournotes.story-manifest/2", "advId": number, "root": "../", "language": "ja", "audio": True, "fonts": "open",
            "story": {"advId": number, "playbackMode": 0, "titles": {"ja": f"Fixture {number}"}, "commands": ["Talk", "Zoom"], "commandCount": 2},
            "requires": {"commands": ["Talk", "Zoom"], "cubismCore": True},
            "files": {
                "story.json": self.asset(story), "episode.json": self.asset(episode, compression="gz"),
                "scene.json": self.asset({"stages": {"fixture_stage": {"nodes": [{"components": [{"class": "AdvStage", "_fov": 45}]}]}}, "shaders": {"names": []}}),
                "audio/sheet_a/cues.json": self.asset({"voice_a": {"file": "voice_a.m4a", "sampleRate": 48000, "channels": 1, "samples": 9600}}),
                "audio/sheet_a/voice_a.m4a": self.wave,
                "raw.json": self.raw, "brotli.json": self.brotli, "split.json": self.split,
            },
            "models": {self.model_id: f"models/{self.model_id}.json"},
            "languages": {"ja": {"files": {}}, "zh-Hans": {"files": {"lang-only.json": self.chinese}}},
            "producerNumber": "__RAW_INFINITY__",
        }

    def publish_stories(self):
        for site, numbers in (("intl", sorted(self.manifests)), ("jp", [7])):
            for number in numbers:
                raw = encode(self.manifests[number]).replace(b'"__RAW_INFINITY__"', b"1e999")
                self.put(f"/{site}/stories/{number}.json", raw)
            self.put(f"/{site}/stories.json", {"format": "ournotes.stories/1", "language": "ja", "languages": ["ja", "zh-Hans"], "stories": [
                {"id": str(number), "advId": number, "manifest": f"stories/{number}.json", "titles": {"ja": f"Fixture {number}"}, "languages": ["ja", "zh-Hans"], "groups": []}
                for number in numbers
            ]})


@contextmanager
def serve(origin):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            path = urlsplit(self.path).path
            with origin.gate:
                origin.requests[path] += 1
                if path in origin.fail_once:
                    origin.fail_once.remove(path)
                    status, body = 503, b"transient fixture error"
                else:
                    body = origin.files.get(path)
                    status = 200 if body is not None else 404
                    if body is None:
                        body = b"not found"
                tag = '"' + hashlib.sha256(body).hexdigest() + '"'
                if status == 200 and self.headers.get("If-None-Match") == tag:
                    status = 304
                origin.responses[status] += 1
            self.send_response(status)
            self.send_header("ETag", tag)
            self.send_header("Content-Length", "0" if status == 304 else str(len(body)))
            self.send_header("Content-Type", "application/json" if path.endswith(".json") else "application/octet-stream")
            self.end_headers()
            if status != 304:
                self.wfile.write(body)

        def log_message(self, *_):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}"
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--managed", action="store_true", help="Keep DOTNET_ROOT for a local development apphost.")
    args = parser.parse_args()
    binary = args.binary.resolve()
    origin = Origin()
    origin.fixture()
    passed = 0

    def check(condition, description):
        nonlocal passed
        if not condition:
            raise AssertionError(description)
        passed += 1

    with tempfile.TemporaryDirectory(prefix="ournotes-cli-archive-") as temp, serve(origin) as endpoint:
        root = Path(temp)
        work = root / "work"
        work.mkdir()
        archive = work / "archive"
        environment = os.environ.copy()
        environment.pop("OURNOTES_ARCHIVE", None)
        environment.update({"OURNOTES_METADATA_BASE": endpoint + "/metadata", "OURNOTES_STORY_SITE_INTL": endpoint + "/intl", "OURNOTES_STORY_SITE_JP": endpoint + "/jp"})
        if not args.managed:
            check(not binary.with_name("advkit.runtimeconfig.json").exists(), "Published Native AOT binary has no runtimeconfig.")
            environment["DOTNET_ROOT"] = str(root / "no-dotnet-installed")
            environment["DOTNET_ROOT_X64"] = environment["DOTNET_ROOT"]

        def run(*command, code=0, cwd=work):
            result = subprocess.run([str(binary), *map(str, command)], cwd=cwd, env=environment, capture_output=True, text=True, encoding="utf-8", timeout=180)
            if result.returncode != code:
                raise AssertionError(f"Command {command!r}: expected exit {code}, got {result.returncode}\n{result.stdout}\n{result.stderr}")
            try:
                document = json.loads(result.stdout)
            except ValueError as error:
                raise AssertionError(f"stdout must contain exactly one JSON result: {result.stdout}\n{result.stderr}") from error
            if code:
                check(document["ok"] is False and document["exitCode"] == code, "Failure has structured JSON and a nonzero exit status.")
            return document

        check(any(command.startswith("archive ") for command in run("help")["commands"]), "Help advertises one archive command.")
        run("archive", "sync", code=2)
        run("archive", "--concurrency", "0", code=2)
        run("archive", "--out", "one", "--archive", "two", code=2)
        initial = run("archive")
        check(initial["ok"] and archive.joinpath("index/stats.json").is_file(), "Bare archive command completes in the default directory.")
        catalog = json.loads(archive.joinpath("index/catalog.json").read_text(encoding="utf-8"))
        check({(row["site"], int(row["advId"])) for row in catalog["stories"]} == {("intl", 7), ("intl", 8), ("jp", 7)}, "MasterData discovery, model cameo discovery and regional selection agree.")
        check(origin.requests["/intl/" + origin.unselected["asset"]] == 0, "Deep scan does not download unrelated model assets.")
        check(origin.responses[503] == 1, "Transient HTTP failure is retried.")
        check(archive.joinpath("sites/intl/stories/7.json").read_bytes() == origin.files["/intl/stories/7.json"], "Original manifest bytes, including 1e999, are retained.")
        for path in ("raw.json", "brotli.json", "split.json", "episode.json"):
            check(run("asset", "verify", path, "--adv", "7")["result"]["verified"], f"Existing C# consumer verifies {path}.")
        check(run("asset", "show", "split.json", "--adv", "7")["result"]["items"] == [1, 2, 3], "Split JSON is reassembled for the existing CLI.")
        check(run("dialogue", "search", "hello", "--with-voice")["result"]["total"] == 2, "Dialogue and voice indexes are usable immediately.")
        check(run("model", "show", origin.model_id)["result"]["motions"] == ["idle"], "Model metadata output works after AOT serialization changes.")
        check(run("stage", "list")["result"]["total"] == 1, "Director Pack is generated by the same archive command.")
        check(run("stage", "show", "fixture_stage")["result"]["definition"] is not None, "Director Pack assets are readable.")
        check(run("commands", "list", "--adv", "7")["result"]["total"] == 2, "Original command index remains compatible.")

        before = origin.asset_requests()
        check(run("archive")["ok"], "Rerun succeeds.")
        check(origin.asset_requests() == before, "Rerun sends zero asset download requests.")
        check(origin.responses[304] > 0, "Mutable metadata is conditionally revalidated with ETags.")
        archive.joinpath("sites/intl", origin.wave["asset"]).unlink()
        run("archive")
        check(origin.asset_requests() == before, "Missing site copy is repaired from the shared store without a download.")
        for file in [archive / "store" / Path(origin.wave["asset"]).name] + [archive / "sites" / site / origin.wave["asset"] for site in ("intl", "jp")]:
            file.write_bytes(b"corrupt")
        run("archive")
        changed_requests = origin.asset_requests() - before
        check(sum(changed_requests.values()) == 1, "Corrupted shared asset is downloaded once and repaired for both sites.")
        check(archive.joinpath("sites/intl", origin.wave["asset"]).read_bytes() == b"original-voice-data", "Repaired asset contains verified original bytes.")

        # A mutable story/model manifest and newly selected episode update independently.
        before = origin.asset_requests()
        added = origin.asset({"revision": 2})
        origin.manifests[7]["files"]["added.json"] = added
        origin.model_manifest["files"]["revision.json"] = added
        for site in ("intl", "jp"):
            origin.put(f"/{site}/models/{origin.model_id}.json", origin.model_manifest)
        origin.manifests[10] = origin.story(10)
        origin.tables["MasterStoryEpisode.json"].append({"_id": 2, "_advId": 10, "_chapterId": 4})
        origin.tables["MasterAdv.json"].append({"_id": 10, "_playbackMode": 0})
        origin.publish_tables()
        origin.publish_stories()
        origin.put("/metadata/current_version.json", {"regions": {"hk-tw-mo": {"version": "v2"}, "jp": {"version": "v2"}}})
        run("archive")
        check(run("story", "list")["result"]["total"] == 3, "New episode is discovered and indexed on an incremental run.")
        check(run("asset", "verify", "added.json", "--adv", "7")["result"]["verified"], "Changed story manifest downloads its new resource.")
        check(run("asset", "verify", "revision.json", "--model", origin.model_id)["result"]["verified"], "Changed model manifest is refreshed, not cached forever.")
        check(all(origin.asset_requests()[path] == count for path, count in before.items()), "Existing content is not downloaded again when upstream grows.")

        # Reuse a legacy-layout archive without the new HTTP cache or command metadata.
        legacy = root / "legacy-project" / "legacy" / "millsage-story-archive" / "archive"
        legacy.mkdir(parents=True)
        for name in ("store", "sites", "index"):
            shutil.copytree(archive / name, legacy / name)
        discovery = json.loads(archive.joinpath("discovery.json").read_text(encoding="utf-8"))
        discovery.pop("archive", None)
        legacy.joinpath("discovery.json").write_bytes(encode(discovery))
        before = origin.asset_requests()
        migrated = run("archive", cwd=root / "legacy-project")
        migrated_archive = Path(migrated["result"]["archive"])
        # Windows TEMP can contain an 8.3 alias such as RUNNER~1 while .NET
        # reports the long directory name. Verify the actual directory identity,
        # not its spelling; a different existing directory must still fail.
        path_details = f"CLI reported {str(migrated_archive)!r}; expected {str(legacy)!r}"
        try:
            same_archive = migrated_archive.samefile(legacy)
        except OSError as error:
            raise AssertionError(f"Cannot verify legacy archive directory identity: {path_details}: {error}") from error
        check(same_archive, f"Existing legacy archive is found automatically: {path_details}.")
        check(origin.asset_requests() == before, "Legacy assets are reused without re-downloading.")

        # The entire update must roll back if downloaded JSON passes hashes but cannot
        # be indexed. Keeping only the old index is insufficient: old manifests matter.
        old_manifest = archive.joinpath("sites/intl/stories/7.json").read_bytes()
        old_index = archive.joinpath("index/catalog.json").read_bytes()
        old_pack = archive.joinpath("sites/intl/director/resources.json").read_bytes()
        old_episode = origin.manifests[7]["files"]["episode.json"]
        invalid_episode = origin.asset(3)
        origin.manifests[7]["files"]["episode.json"] = invalid_episode
        origin.publish_stories()
        try:
            run("archive", code=3)
            check(archive.joinpath("sites/intl/stories/7.json").read_bytes() == old_manifest, "Failed indexing rolls back the live story manifest.")
            check(archive.joinpath("index/catalog.json").read_bytes() == old_index, "Failed indexing preserves the previous searchable catalog.")
            check(archive.joinpath("sites/intl/director/resources.json").read_bytes() == old_pack, "Failed indexing preserves the previous Director Pack.")
            check(run("story", "commands", "7")["result"]["total"] == 2, "The previous archive remains readable after a failed update.")
            check(archive.joinpath("store", Path(invalid_episode["asset"]).name).exists(), "Rollback retains downloaded immutable assets for a later run.")
        finally:
            origin.manifests[7]["files"]["episode.json"] = old_episode
            origin.publish_stories()

        filtered = root / "ja-only"
        before = origin.asset_requests()
        run("archive", "--out", filtered, "--sites", "intl", "--languages", "ja", "--no-deep")
        check(not filtered.joinpath("sites/jp").exists(), "Single-site selection does not create another regional archive.")
        check(not filtered.joinpath("sites/intl", origin.chinese["asset"]).exists(), "Language filter avoids unrelated language payloads.")
        check(not filtered.joinpath("sites/intl/stories/8.json").exists(), "--no-deep excludes the model-only cameo.")
        check(origin.asset_requests()["/intl/" + origin.chinese["asset"]] == before["/intl/" + origin.chinese["asset"]], "Unselected language payload is not requested.")

        for number in (7, 10):
            origin.manifests[number]["story"]["playbackMode"] = 1
        origin.publish_stories()
        try:
            simple = run("archive", "--out", filtered, "--sites", "intl", "--languages", "ja", "--no-deep")
            check(simple["result"]["director"][0]["skipped"], "SimpleADV-only selection completes without an unusable normal pack.")
            check(run("stage", "list", "--archive", filtered)["result"]["total"] == 0, "Successful scope change does not expose the previous pack's stale resources.")
        finally:
            for number in (7, 10):
                origin.manifests[number]["story"]["playbackMode"] = 0
            origin.publish_stories()

        interrupted = root / "interrupted"
        missing_path = "/intl/" + added["asset"]
        saved = origin.files.pop(missing_path)
        try:
            run("archive", "--out", interrupted, "--sites", "intl", "--no-deep", "--retries", "1", code=3)
        finally:
            origin.files[missing_path] = saved
        check(not interrupted.joinpath("index/stats.json").exists(), "Failed acquisition does not publish a successful index.")
        run("archive", "--out", interrupted, "--sites", "intl", "--no-deep")
        check(interrupted.joinpath("index/stats.json").exists(), "Rerun completes an interrupted archive.")

    print(json.dumps({"ok": True, "checks": passed, "binary": str(binary), "nativeAot": not args.managed}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
