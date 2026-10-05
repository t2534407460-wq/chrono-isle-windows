"""Publish only a fixed ChronoIsle installer and checksum, following 21day."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.parse
import urllib.request

OWNER = "t2534407460-wq"
REPOSITORY = OWNER + "/chrono-isle-windows-releases"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--installer", type=Path, required=True)
    parser.add_argument("--notes", type=Path, required=True)
    parser.add_argument("--publish", action="store_true")
    args = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version):
        raise SystemExit("A three-part numeric version is required.")
    expected_name = "ChronoIsle-Setup-" + args.version + "-win-x64.exe"
    if args.installer.name != expected_name or not args.installer.is_file():
        raise SystemExit("Installer path/name does not match the release version.")
    binary = args.installer.read_bytes()
    checksum = hashlib.sha256(binary).hexdigest()
    notes = args.notes.read_text(encoding="utf-8-sig").rstrip()
    if "ChronoIsle " + args.version not in notes:
        raise SystemExit("Release notes do not identify the requested version.")
    notes += "\n\nSHA-256: `" + checksum + "`\n"
    token = os.environ.get("CHRONOISLE_RELEASE_TOKEN")
    if not token:
        env = dict(os.environ, GIT_TERMINAL_PROMPT="0", GCM_INTERACTIVE="never")
        result = subprocess.run(
            ["git", "credential", "fill"],
            input=f"protocol=https\nhost=github.com\nusername={OWNER}\n\n",
            text=True, capture_output=True, env=env, timeout=30,
        )
        credentials = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)
        token = credentials.get("password")
    if not token:
        raise SystemExit("The release account must be logged into Git Credential Manager.")

    def request(method, path, payload=None, raw=None, content_type=None, anonymous=False):
        url = path if path.startswith("https://uploads.github.com/") else "https://api.github.com" + path
        body = raw if raw is not None else json.dumps(payload).encode() if payload is not None else None
        headers = {"Accept": "application/vnd.github+json", "User-Agent": "ChronoIsle-release",
                   "Content-Type": content_type or "application/json"}
        if not anonymous:
            headers["Authorization"] = "Bearer " + token
        try:
            with urllib.request.urlopen(urllib.request.Request(url, data=body, headers=headers, method=method), timeout=180) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            raise SystemExit(f"GitHub request failed: {method} HTTP {error.code}.") from None
        except urllib.error.URLError:
            raise SystemExit("GitHub request failed; verify network access and retry the same release.") from None

    if request("GET", "/user")["login"] != OWNER:
        raise SystemExit("Signed-in account does not match the release owner.")
    repo = request("GET", "/repos/" + REPOSITORY)
    if repo["private"] or not repo.get("permissions", {}).get("push"):
        raise SystemExit("A public writable release repository is required.")
    tree = request("GET", "/repos/" + REPOSITORY + "/git/trees/" + repo["default_branch"] + "?recursive=1")
    if tree.get("truncated") or [item["path"] for item in tree["tree"]] != ["README.md"]:
        raise SystemExit("The public repository must contain only README.md.")
    releases = request("GET", "/repos/" + REPOSITORY + "/releases?per_page=100")
    release = next((item for item in releases if item["tag_name"] == "v" + args.version), None)
    if release is None:
        release = request("POST", "/repos/" + REPOSITORY + "/releases", {
            "tag_name": "v" + args.version, "name": "时屿 ChronoIsle " + args.version,
            "body": notes, "draft": True, "prerelease": False,
            "target_commitish": repo["default_branch"],
        })
    assets_path = "/repos/" + REPOSITORY + "/releases/" + str(release["id"]) + "/assets"
    assets = request("GET", assets_path)
    sums = (checksum + "  " + expected_name + "\n").encode()
    files = [(expected_name, binary, "application/vnd.microsoft.portable-executable"),
             ("SHA256SUMS.txt", sums, "text/plain")]
    if any(asset["name"] not in {name for name, _, _ in files} for asset in assets):
        raise SystemExit("Unexpected assets found; review the existing release before continuing.")
    for name, contents, content_type in files:
        digest = "sha256:" + hashlib.sha256(contents).hexdigest()
        asset = next((item for item in assets if item["name"] == name), None)
        if asset is None:
            if not release["draft"]:
                raise SystemExit("Published release is missing an expected asset; use a new version.")
            upload = release["upload_url"].split("{")[0] + "?name=" + urllib.parse.quote(name)
            asset = request("POST", upload, raw=contents, content_type=content_type)
        if asset.get("digest") != digest or asset["size"] != len(contents):
            raise SystemExit("Asset verification failed; do not replace an existing asset: " + name)
    if args.publish:
        if release["draft"]:
            release = request("PATCH", "/repos/" + REPOSITORY + "/releases/" + str(release["id"]),
                              {"body": notes, "draft": False, "prerelease": False, "make_latest": "true"})
        latest = request("GET", "/repos/" + REPOSITORY + "/releases/latest", anonymous=True)
        if latest["tag_name"] != "v" + args.version or latest["draft"] or latest["prerelease"]:
            raise SystemExit("Anonymous latest-release verification failed.")
        public_assets = {asset["name"]: asset for asset in latest["assets"]}
        for name, contents, _ in files:
            asset = public_assets.get(name, {})
            if asset.get("digest") != "sha256:" + hashlib.sha256(contents).hexdigest() or asset.get("size") != len(contents):
                raise SystemExit("Anonymous asset verification failed: " + name)
    print(json.dumps({"url": release["html_url"], "draft": release["draft"],
                      "installer": expected_name, "size": len(binary), "sha256": checksum}, ensure_ascii=False))


if __name__ == "__main__":
    main()
