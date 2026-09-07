#!/usr/bin/env python3
"""Install exact multi-target releases, then advance Catalog after public-byte verification.

Runs on the Registry server. The catalog phase deliberately does no networking: the release
workflow must verify every public URL between install and catalog. No Host is restarted.
"""
import argparse
import copy
import fcntl
import hashlib
import json
from pathlib import Path
import re
import shutil
import sys
import tempfile


def version_key(value):
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", value):
        raise ValueError("Release version must be stable X.Y.Z")
    return tuple(map(int, value.split(".")))


def component(value):
    if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9._-]*", value) or ".." in value:
        raise ValueError("Invalid release path component")
    return value


def target_key(item):
    return (component(item["target"]["os"]), component(item["target"]["arch"]))


def validate_entry(entry):
    component(entry["packageId"])
    if not entry["displayName"] or not entry["summary"]:
        raise ValueError("Catalog presentation is required")
    targets = [target_key(item) for item in entry["latest"]]
    if len(targets) != len(set(targets)):
        raise ValueError("Catalog targets must be unique")
    for item in entry["latest"]:
        version_key(item["version"])


def exact_files(source, entry):
    releases = []
    for item in entry["latest"]:
        os, arch = target_key(item)
        target = f"{os}-{arch}"
        metadata_bytes = (source / target / "release.json").read_bytes()
        metadata = json.loads(metadata_bytes)
        if (metadata["schemaVersion"] != 1 or metadata["packageId"] != entry["packageId"] or
                metadata["version"] != item["version"] or metadata["target"] != item["target"]):
            raise ValueError("Release metadata and Catalog must identify the same Package/target")
        artifact = metadata["artifact"]
        name = component(artifact["fileName"])
        content = (source / name).read_bytes()
        if len(content) != artifact["length"] or "sha256:" + hashlib.sha256(content).hexdigest() != artifact["sha256"]:
            raise ValueError("Release artifact integrity mismatch")
        suffix = f"/packages/{entry['packageId']}/versions/{item['version']}/{target}/release.json"
        if not item["releaseUrl"].endswith(suffix) or artifact["url"] != item["releaseUrl"].removesuffix("release.json") + name:
            raise ValueError("Release URLs must match exact Package/target paths")
        path = Path("packages") / entry["packageId"] / "versions" / item["version"] / target
        releases.append((path, {name: content, "release.json": metadata_bytes}))
    if not releases:
        raise ValueError("Incoming release must contain at least one target")
    return releases


def verify_installed(target, files):
    if not target.is_dir() or any(not (target / name).is_file() or (target / name).read_bytes() != data for name, data in files.items()):
        raise ValueError(f"Refusing missing or different immutable release bytes: {target}")


def publish(source, registry, phase):
    entry = json.loads((source / "catalog-entry.json").read_bytes())
    validate_entry(entry)
    releases = exact_files(source, entry)
    registry.mkdir(parents=True, exist_ok=True)
    # Same flock file as the existing Collector publisher: different packages cannot lose
    # each other's Catalog updates, and concurrent reruns cannot overwrite immutable bytes.
    with (registry / ".catalog.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        for relative, files in releases:
            target = registry / relative
            if target.exists() or phase == "catalog":
                verify_installed(target, files)
        if phase == "install":
            for relative, files in releases:
                target = registry / relative
                if target.exists():
                    continue
                target.parent.mkdir(parents=True, exist_ok=True)
                pending = Path(tempfile.mkdtemp(prefix=f".{target.name}-", dir=target.parent))
                try:
                    pending.chmod(0o755)
                    for name, data in files.items():
                        (pending / name).write_bytes(data)
                        (pending / name).chmod(0o644)
                    pending.rename(target)
                finally:
                    if pending.exists():
                        shutil.rmtree(pending)
            return

        catalog_path = registry / "catalog.json"
        catalog = json.loads(catalog_path.read_bytes()) if catalog_path.exists() else {"schemaVersion": 1, "packages": []}
        if catalog["schemaVersion"] != 1:
            raise ValueError("Unsupported Catalog version")
        for existing in catalog["packages"]:
            validate_entry(existing)
        packages = {value["packageId"]: value for value in catalog["packages"]}
        if len(packages) != len(catalog["packages"]):
            raise ValueError("Catalog Package ids must be unique")
        previous = packages.get(entry["packageId"])
        merged = copy.deepcopy(previous or entry)
        latest = {target_key(item): item for item in merged["latest"]}
        for incoming in entry["latest"]:
            key = target_key(incoming)
            current = latest.get(key)
            if current and version_key(current["version"]) > version_key(incoming["version"]):
                continue
            if current and current["version"] == incoming["version"] and current != incoming:
                raise ValueError("Refusing different Catalog metadata for the same exact release")
            if previous and any(previous[field] != entry[field] for field in ("displayName", "summary")):
                raise ValueError("Refusing to change shared Catalog presentation across targets")
            latest[key] = incoming
        merged["latest"] = list(latest.values())
        if merged == previous:
            return
        packages[entry["packageId"]] = merged
        catalog["packages"] = list(packages.values())
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", prefix=".catalog-", dir=registry, delete=False) as temp:
            temporary = Path(temp.name)
            json.dump(catalog, temp, ensure_ascii=False, indent=2)
            temp.write("\n")
        try:
            temporary.chmod(0o644)
            temporary.replace(catalog_path)
        finally:
            temporary.unlink(missing_ok=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release", type=Path, required=True)
    parser.add_argument("--registry", type=Path, required=True)
    parser.add_argument("--phase", choices=("install", "catalog"), required=True)
    args = parser.parse_args()
    try:
        publish(args.release, args.registry, args.phase)
    except (ValueError, KeyError, OSError) as error:
        sys.exit(str(error))
