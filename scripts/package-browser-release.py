#!/usr/bin/env python3
"""Create one deterministic Browser zip and four exact Web releases; never upload anything."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
from urllib.parse import urlsplit
import zipfile

PACKAGE_ID = "heartbeat.collector.browser"
TARGETS = [(os, arch) for os in ("windows", "macos") for arch in ("x64", "arm64")]


def digest(data):
    return "sha256:" + hashlib.sha256(data).hexdigest()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def assemble(package, version, output, base_url):
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version):
        raise ValueError("Version must be stable X.Y.Z")
    parsed_url = urlsplit(base_url)
    if parsed_url.scheme != "https" or not parsed_url.netloc or parsed_url.query or parsed_url.fragment:
        raise ValueError("Registry base URL must be HTTPS without query or fragment")
    package = package.resolve(strict=True)
    if output.is_symlink():
        raise ValueError("Output must not be a symbolic link")
    output = output.resolve()
    if output == package or package in output.parents or output in package.parents:
        raise ValueError("Output must not overlap Package")
    if output.exists() and (not output.is_dir() or any(output.iterdir())):
        raise ValueError("Output must be new or empty")
    files = {}
    for path in sorted(package.rglob("*")):
        if path.is_symlink():
            raise ValueError("Package must not contain symbolic links")
        if path.is_file():
            files[path.relative_to(package).as_posix()] = path.read_bytes()
    manifest_bytes = files["collector-manifest.json"]
    manifest = json.loads(manifest_bytes)
    if manifest["packageId"] != PACKAGE_ID or manifest["version"] != version:
        raise ValueError("Package identity/version must match the Browser tag")
    presentation = manifest["presentation"]
    if not presentation["displayName"] or not presentation["summary"]:
        raise ValueError("Package must declare Marketplace presentation")
    if manifest["defaultInstance"]["subjectKind"] != "machine":
        raise ValueError("Browser default Instance must use Machine Subject")
    if len(manifest["artifacts"]) != 1:
        raise ValueError("Browser must have one ExternalHost artifact")
    artifact = manifest["artifacts"][0]
    if artifact["selector"] != {"driver": "externalHost", "os": ["windows", "macos"], "arch": ["x64", "arm64"]}:
        raise ValueError("Browser artifact must select exactly the four desktop targets")
    descriptor_bytes = files[artifact["entrypoint"]]
    if digest(descriptor_bytes) != artifact["contentHash"] or len(descriptor_bytes) != artifact["size"]:
        raise ValueError("Artifact descriptor integrity mismatch")
    descriptor = json.loads(descriptor_bytes)
    payload_paths = []
    for item in descriptor["files"]:
        content = files[item["path"]]
        if len(content) != item["size"] or digest(content) != item["contentHash"]:
            raise ValueError("Extension payload integrity mismatch")
        payload_paths.append(item["path"])
    reference_path = "browser-extension/collector-artifact-ref.json"
    expected_payload = {name for name in files if name.startswith("browser-extension/") and name != reference_path}
    if len(set(payload_paths)) != len(payload_paths) or set(payload_paths) != expected_payload:
        raise ValueError("Artifact descriptor must enumerate the complete extension payload")
    if json.loads(files[reference_path]) != {
        "packageId": PACKAGE_ID, "packageVersion": version, "packageContentHash": digest(manifest_bytes),
        "artifactId": artifact["artifactId"], "artifactHash": artifact["contentHash"],
    }:
        raise ValueError("Bootstrap reference does not match the final Package")
    if json.loads(files["browser-extension/manifest.json"])["version"] != version:
        raise ValueError("Extension version must match the Browser tag")

    output.mkdir(parents=True, exist_ok=True)
    artifact_name = f"{PACKAGE_ID}-{version}.zip"
    # Fixed timestamps, permissions, ordering and STORE avoid OS/zlib-dependent output bytes.
    with zipfile.ZipFile(output / artifact_name, "w") as archive:
        for name, content in sorted(files.items()):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            archive.writestr(info, content)
    content = (output / artifact_name).read_bytes()
    latest = []
    for os, arch in TARGETS:
        target = {"os": os, "arch": arch}
        url = f"{base_url.rstrip('/')}/packages/{PACKAGE_ID}/versions/{version}/{os}-{arch}"
        write_json(output / f"{os}-{arch}" / "release.json", {
            "schemaVersion": 1, "packageId": PACKAGE_ID, "version": version, "target": target,
            "artifact": {"fileName": artifact_name, "url": f"{url}/{artifact_name}",
                         "length": len(content), "sha256": digest(content)},
        })
        latest.append({"version": version, "target": target, "releaseUrl": f"{url}/release.json"})
    write_json(output / "catalog-entry.json", {"packageId": PACKAGE_ID, **presentation, "latest": latest})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--base-url", default="https://heartbeat.shenxianovo.com/collector-registry/v1")
    args = parser.parse_args()
    assemble(args.package, args.version, args.output, args.base_url)
    print(f"Browser release ready: {args.output}")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError) as error:
        sys.exit(str(error))
