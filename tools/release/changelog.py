# tools/release/changelog.py
"""Parse CHANGELOG.md (Keep a Changelog format) into structured data."""
from __future__ import annotations

import re

_VERSION_RE = re.compile(r"^##\s*\[(?P<ver>[^\]]+)\]\s*(?:-\s*(?P<date>\S+))?\s*$")
_SUBSECTION_RE = re.compile(r"^###\s*(?P<name>.+?)\s*$")
_BULLET_RE = re.compile(r"^\s*-\s+(?P<text>.+?)\s*$")
_SECTIONS = ("added", "changed", "fixed", "removed")


def _iter_version_blocks(text: str):
    """Yield (version, date, [body_lines]) for each '## [x] - date' block."""
    current = None
    body: list[str] = []
    for line in text.splitlines():
        m = _VERSION_RE.match(line)
        if m:
            if current is not None:
                yield current[0], current[1], body
            current = (m.group("ver"), m.group("date"))
            body = []
        elif current is not None:
            body.append(line)
    if current is not None:
        yield current[0], current[1], body


def latest_version(text: str) -> str:
    """First released version heading, skipping 'Unreleased'."""
    for ver, _date, _body in _iter_version_blocks(text):
        if ver.lower() != "unreleased":
            return ver
    raise ValueError("no released version found in changelog")


def parse_changelog(text: str, version: str | None = None) -> dict:
    if version is None:
        version = latest_version(text)
    for ver, date, body in _iter_version_blocks(text):
        if ver == version:
            entry = {"version": ver, "date": date or "", **{s: [] for s in _SECTIONS}}
            bucket = None
            for line in body:
                sm = _SUBSECTION_RE.match(line)
                if sm:
                    bucket = sm.group("name").lower()   # reset on EVERY ### heading
                    continue
                bm = _BULLET_RE.match(line)
                if bm and bucket in _SECTIONS:           # only collect known sections
                    entry[bucket].append(bm.group("text"))
            return entry
    raise KeyError(f"version {version!r} not found in changelog")
