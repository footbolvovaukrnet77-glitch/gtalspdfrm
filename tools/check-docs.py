#!/usr/bin/env python3
"""Checks the documentation the way a reader would.

Three things break silently and are caught here rather than by someone clicking
a dead link:

1. A relative link that points at a file which does not exist.
2. A `#anchor` that names a heading which is not in the target file. This is the
   one that rots fastest, because renaming a heading is a normal edit and
   nothing in the editor complains.
3. An English document with no Russian counterpart, or the reverse. Every
   document in `docs/` is supposed to exist in both languages and say so at the
   top; a half-translated set is worse than an untranslated one, because the
   reader cannot tell which pages they are missing.

Exit code 0 when clean, 1 with a list of problems otherwise. No dependencies
beyond the standard library, so CI needs nothing installed.
"""

import io
import os
import re
import sys

LINK = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")
HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*$")
FENCE = re.compile(r"^\s*(```|~~~)")

# GitHub's anchor rules: lowercase, drop everything that is not a word
# character, a space or a hyphen, then spaces become hyphens. Cyrillic is a word
# character for our purposes -- the Russian documents rely on it.
ANCHOR_STRIP = re.compile(r"[^\w\s\-]", re.UNICODE)
INLINE_MARKUP = re.compile(r"[`*_]")


def anchors_of(path):
    """Every heading anchor a Markdown file offers, GitHub's slug rules."""
    found = set()
    in_fence = False
    for line in io.open(path, encoding="utf-8"):
        if FENCE.match(line):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        match = HEADING.match(line)
        if not match:
            continue
        text = INLINE_MARKUP.sub("", match.group(2)).lower()
        text = LINK.sub(lambda m: m.group(0), text)
        found.add(ANCHOR_STRIP.sub("", text).strip().replace(" ", "-"))
    return found


def markdown_files(root):
    skip = {".git", "bin", "obj", "dist", "artifacts", "node_modules"}
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in skip]
        for name in sorted(filenames):
            if name.endswith(".md"):
                yield os.path.join(dirpath, name)


def check_links(root, files, problems):
    anchor_cache = {}
    for path in files:
        rel = os.path.relpath(path, root)
        text = io.open(path, encoding="utf-8").read()
        for target in LINK.findall(text):
            if target.startswith(("http://", "https://", "mailto:", "#!")):
                continue
            filepart, _, anchor = target.partition("#")
            if filepart:
                resolved = os.path.normpath(
                    os.path.join(os.path.dirname(path), filepart))
                if not os.path.exists(resolved):
                    problems.append("%s: link to a missing file -> %s" % (rel, target))
                    continue
            else:
                resolved = path
            if not anchor:
                continue
            if not resolved.endswith(".md"):
                continue
            if resolved not in anchor_cache:
                anchor_cache[resolved] = anchors_of(resolved)
            if anchor.lower() not in anchor_cache[resolved]:
                problems.append("%s: link to a missing heading -> %s" % (rel, target))


def check_translation_pairs(root, problems):
    english_dir = os.path.join(root, "docs")
    russian_dir = os.path.join(english_dir, "ru")
    if not os.path.isdir(russian_dir):
        problems.append("docs/ru is missing entirely")
        return

    english = {f for f in os.listdir(english_dir) if f.endswith(".md")}
    russian = {f for f in os.listdir(russian_dir) if f.endswith(".md")}

    for name in sorted(english - russian):
        problems.append("docs/%s has no Russian counterpart in docs/ru/" % name)
    for name in sorted(russian - english):
        problems.append("docs/ru/%s has no English counterpart in docs/" % name)

    # The top-level pairs are named by suffix rather than by directory.
    for base in ("README", "DEV_COMMANDS", "TROUBLESHOOTING"):
        pair = [base + ".md", base + ".ru.md"]
        missing = [p for p in pair if not os.path.exists(os.path.join(root, p))]
        for name in missing:
            problems.append("%s is missing (its counterpart exists)" % name)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    files = list(markdown_files(root))
    problems = []

    check_links(root, files, problems)
    check_translation_pairs(root, problems)

    print("checked %d markdown files" % len(files))
    for problem in problems:
        print("  BROKEN: %s" % problem)

    if problems:
        print("%d problem(s)" % len(problems))
        return 1
    print("no broken links, no missing translations")
    return 0


if __name__ == "__main__":
    sys.exit(main())
