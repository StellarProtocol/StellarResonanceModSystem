# tools/release/make_version_json.py
"""Generate version.json (FrameworkManifest) for the StellarResonance launcher.

Emits the history-bearing shape — { latest, channel, versions[] } — so the launcher can
offer a version picker. Each release is APPENDED to the existing published history (passed
via --existing); old entries keep their original bundleUrl + sha256 so users can roll back.
See the manifest standard in docs/manifest-standard.md.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from changelog import parse_changelog


def _sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _ver_key(v: str) -> tuple:
    """Sort key: numeric per dotted component, leading 'v' tolerated."""
    return tuple(int(p) if p.isdigit() else 0 for p in v.lstrip("vV").split("."))


def build_entry(changelog_text: str, bundle_path: Path,
                bundle_url: str, min_launcher_version: str,
                version: str | None = None) -> dict:
    """One release entry (an element of versions[])."""
    entry = parse_changelog(changelog_text, version)
    return {
        "version": entry["version"],
        "date": entry["date"],
        "bundleUrl": bundle_url,
        "sha256": _sha256(bundle_path),
        "minLauncherVersion": min_launcher_version,
        "changelog": {
            "added": entry["added"],
            "changed": entry["changed"],
            "fixed": entry["fixed"],
            "removed": entry["removed"],
        },
    }


def build_manifest(entry: dict, channel: str, existing: dict | None) -> dict:
    """Merge a new release entry into the existing history → { latest, channel, versions[] }."""
    versions = [entry]
    for prev in (existing or {}).get("versions", []):
        if prev.get("version") != entry["version"]:   # a re-release of the same version supersedes the old one
            versions.append(prev)
    versions.sort(key=lambda v: _ver_key(v["version"]), reverse=True)
    return {"latest": versions[0]["version"], "channel": channel, "versions": versions}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--changelog", required=True, type=Path)
    ap.add_argument("--bundle", required=True, type=Path)
    ap.add_argument("--bundle-url", required=True)
    ap.add_argument("--min-launcher", required=True)
    ap.add_argument("--version", default=None)
    ap.add_argument("--channel", default="stable")
    ap.add_argument("--existing", type=Path, default=None,
                    help="current published version.json to append to (history is preserved)")
    ap.add_argument("--out", required=True, type=Path)
    args = ap.parse_args()

    entry = build_entry(
        changelog_text=args.changelog.read_text(encoding="utf-8"),
        bundle_path=args.bundle,
        bundle_url=args.bundle_url,
        min_launcher_version=args.min_launcher,
        version=args.version,
    )

    existing = None
    if args.existing and args.existing.exists():
        text = args.existing.read_text(encoding="utf-8").strip()
        if text:
            existing = json.loads(text)

    manifest = build_manifest(entry, args.channel, existing)
    args.out.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {args.out} (latest {manifest['latest']}, {len(manifest['versions'])} versions)")


if __name__ == "__main__":
    main()
