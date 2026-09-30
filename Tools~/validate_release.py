#!/usr/bin/env python3
"""Validate release-facing metadata and documentation for Avatar Part Assembler.

This check is intentionally small and dependency-free so it can run before a tag is
created, without opening Unity or requiring a VPM environment.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path


PACKAGE_ROOT = Path(__file__).resolve().parents[1]


def fail(message: str) -> None:
    raise SystemExit(f"FAIL: {message}")


def read(relative: str) -> str:
    path = PACKAGE_ROOT / relative
    if not path.is_file():
        fail(f"missing file: {relative}")
    return path.read_text(encoding="utf-8")


def check(condition: bool, message: str) -> None:
    if not condition:
        fail(message)
    print(f"PASS: {message}")


def check_markdown_links(relative: str) -> None:
    source = read(relative)
    source_path = PACKAGE_ROOT / relative
    for target in re.findall(r"\[[^\]]+\]\(([^)]+)\)", source):
        target = target.strip().split("#", 1)[0]
        if not target or target.startswith(("http://", "https://", "mailto:")):
            continue
        check((source_path.parent / target).resolve().is_file(), f"link {relative} -> {target}")


def main() -> int:
    package = json.loads(read("package.json"))
    version = package.get("version")
    check(bool(re.fullmatch(r"\d+\.\d+\.\d+", str(version))), "package version is stable semver")

    profile = read("Runtime/Profiles/ApaPartProfile.cs")
    schema_match = re.search(r"CurrentSchemaVersion\s*=\s*(\d+)", profile)
    check(schema_match is not None, "profile schema constant is present")
    schema = schema_match.group(1)

    docs = {
        "README.md": read("README.md"),
        "README.zh-CN.md": read("README.zh-CN.md"),
        "Documentation~/OVERVIEW.md": read("Documentation~/OVERVIEW.md"),
        "Documentation~/PART_AUTHORING_GUIDE.zh-CN.md": read("Documentation~/PART_AUTHORING_GUIDE.zh-CN.md"),
        "USER_ACCEPTANCE_CHECKLIST.md": read("USER_ACCEPTANCE_CHECKLIST.md"),
        "Third Party Notices.md": read("Third Party Notices.md"),
    }
    versioned_docs = {name: content for name, content in docs.items() if name != "Third Party Notices.md"}
    for name, content in versioned_docs.items():
        check(version in content, f"{name} mentions package version {version}")

    check(f"#v{version}" in docs["README.md"], "README tag example matches package version")
    check(f"schema version {schema}" in docs["Documentation~/OVERVIEW.md"], "overview matches profile schema")
    check("schema version 5" in docs["Documentation~/OVERVIEW.md"], "overview documents schema-5 authoring")
    check("nadena.dev.ndmf` | 1.14.8" in docs["Third Party Notices.md"], "third-party notice matches resolved NDMF")
    check("nadena.dev.modular-avatar` | 1.19.0-alpha.0" in docs["Third Party Notices.md"], "third-party notice matches resolved Modular Avatar")

    for name, content in [("package.json", read("package.json")), *docs.items()]:
        for stale in ("0.3.0-rc.6", "0.4.0", "0.5.3", "authenticated encrypted payload", "经过认证的加密载荷"):
            check(stale not in content, f"{name} has no stale release/security wording: {stale}")

    description = package.get("description", "")
    check("not DRM" in description, "package description states the protected-mode boundary")
    check("obfuscated" in description and "integrity-checked" in description,
          "package description uses honest protected-mode terminology")

    acceptance = docs["USER_ACCEPTANCE_CHECKLIST.md"]
    protected_rows = re.findall(r"^\| 14\.\d+ \|", acceptance, re.MULTILINE)
    check(len(protected_rows) == 10, "protected-mode acceptance group contains 10 rows")
    check("| 14. Protected part mesh mode | 10 |" in acceptance,
          "acceptance summary matches protected-mode row count")

    check_markdown_links("README.md")
    check_markdown_links("README.zh-CN.md")
    check_markdown_links("Documentation~/OVERVIEW.md")
    check_markdown_links("Documentation~/PART_AUTHORING_GUIDE.zh-CN.md")
    check_markdown_links("USER_ACCEPTANCE_CHECKLIST.md")
    print(f"Release metadata is consistent for Avatar Part Assembler {version} (schema {schema}).")
    return 0


if __name__ == "__main__":
    sys.exit(main())

