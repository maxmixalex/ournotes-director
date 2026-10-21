#!/usr/bin/env python3
"""Serve a bounded real archive over loopback for the packaged GUI download test.

Only discovery metadata is synthetic. Story/model manifests and every requested
payload are the exact previously fetched fixture files. No public origin is used.
"""
import argparse
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlsplit


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", type=Path)
    parser.add_argument("ready", type=Path)
    args = parser.parse_args()
    archive = args.archive.resolve()
    index = json.loads((archive / "sites/intl/stories.json").read_text(encoding="utf-8"))
    # The fixture preserves the original site's full index, while its bounded
    # local catalog identifies the payloads fetched for this test.
    catalog = json.loads((archive / "index/catalog.json").read_text(encoding="utf-8"))
    local_ids = {str(row["advId"]) for row in catalog["stories"] if row["site"] == "intl"}
    stories = [row for row in index["stories"] if str(row["advId"]) in local_ids]
    if not stories:
        raise ValueError("The resource fixture has no local international stories")
    index["stories"] = stories
    tables = {
        "MasterBand.json": [{"_id": 4, "_nameTextID": "Band_Name_millsage"}],
        "MasterCharacter.json": [{"_id": 16, "_bandID": 4, "_nameTextID": "Character_Name_hotaru"}],
        "MasterStoryChapter.json": [{"_id": 4, "_bandId": 4, "_mainCharacterIds": [16]}],
        "MasterStoryEpisode.json": [{"_id": i + 1, "_advId": int(row["advId"]), "_chapterId": 4} for i, row in enumerate(stories)],
        "MasterAdv.json": [{"_id": int(row["advId"])} for row in stories],
        "MasterStoryFriendshipEpisode.json": [], "MasterCharacterFriendship.json": [],
        "MasterStoryLiveResultEpisode.json": [], "MasterStoryHomeSpotTapTalkEpisode.json": [], "MasterHomeSpot.json": [],
    }
    responses = {"/metadata/current_version.json": {"regions": {"hk-tw-mo": {"version": "local-gui-fixture"}}}}
    responses["/intl/stories.json"] = index
    responses.update({"/metadata/tw/master/" + name: rows for name, rows in tables.items()})
    responses = {key: json.dumps(value, ensure_ascii=False).encode("utf-8") for key, value in responses.items()}
    counters = {"metadata": 0, "manifests": 0, "assets": 0, "missing": 0}
    lock = threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            route = unquote(urlsplit(self.path).path)
            payload = responses.get(route)
            bucket = "metadata"
            if route == "/_stats":
                with lock:
                    payload = json.dumps(counters).encode("utf-8")
            elif payload is None and route.startswith("/intl/"):
                relative = route[len("/intl/"):]
                source = (archive / "sites/intl" / relative).resolve()
                if source.is_relative_to(archive / "sites/intl") and source.is_file():
                    payload = source.read_bytes()
                    bucket = "assets" if relative.startswith("assets/") else "manifests"
            if payload is None:
                with lock:
                    counters["missing"] += 1
                self.send_error(404)
                return
            with lock:
                if route != "/_stats":
                    counters[bucket] += 1
            if bucket == "assets":
                time.sleep(.01)  # Make cancellation during actual byte transfer deterministic.
            self.send_response(200)
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("Content-Type", "application/octet-stream")
            self.end_headers()
            try:
                self.wfile.write(payload)
            except (BrokenPipeError, ConnectionResetError):
                pass  # The real cancellation test deliberately closes downloads.

        def log_message(self, *_):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    origin = "http://127.0.0.1:" + str(server.server_address[1])
    args.ready.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.ready.with_suffix(".tmp")
    temporary.write_text(json.dumps({"origin": origin, "stories": len(stories), "archive": str(archive)}), encoding="utf-8")
    temporary.replace(args.ready)
    server.serve_forever()


if __name__ == "__main__":
    main()
