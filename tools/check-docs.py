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
4. A protocol version printed in the documentation that is not the one the code
   actually sends. Both protocol documents said **5** while the constant had
   been at **8** for some time: a reader implementing against the document would
   have been rejected at the handshake and told the version they had just read
   was wrong.
5. A test count printed in the documentation that is not the number the suite
   actually has. Six documents state it, in five different phrasings and two
   languages, and every one of them was maintained by hand — which is to say by
   remembering. The count is derivable: xUnit runs one case per `[Fact]` and one
   per `[InlineData]`, so it can be counted from the sources and compared.

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


PROTOCOL_IN_DOC = re.compile(r"(?:Protocol version|Версия протокола): \*\*(\d+)\*\*")
PROTOCOL_IN_CODE = re.compile(r"ProtocolVersion\s*=\s*(\d+)\s*;")


def check_protocol_version(root, problems):
    """The version in the protocol documents must be the one the code sends."""
    source = os.path.join(root, "src", "Gtamp.Shared", "Protocol", "ProtocolConstants.cs")
    if not os.path.exists(source):
        problems.append("src/Gtamp.Shared/Protocol/ProtocolConstants.cs is missing")
        return

    with io.open(source, encoding="utf-8") as handle:
        match = PROTOCOL_IN_CODE.search(handle.read())

    if match is None:
        problems.append("ProtocolConstants.cs no longer declares ProtocolVersion in a readable form")
        return

    expected = match.group(1)
    found_anywhere = False

    for name in ("NETWORK_PROTOCOL.md", os.path.join("ru", "NETWORK_PROTOCOL.md")):
        path = os.path.join(root, "docs", name)
        if not os.path.exists(path):
            continue

        with io.open(path, encoding="utf-8") as handle:
            text = handle.read()

        for stated in PROTOCOL_IN_DOC.findall(text):
            found_anywhere = True
            if stated != expected:
                problems.append(
                    "docs/%s says protocol version %s; the code sends %s"
                    % (name.replace(os.sep, "/"), stated, expected))

    if not found_anywhere:
        problems.append(
            "neither protocol document states a version any more — the check above cannot work")


TEST_COUNT_IN_DOC = (
    re.compile(r"Passed:\s*(\d+)"),
    re.compile(r"All (\d+) tests"),
    re.compile(r"Все (\d+) тест\w*"),
    re.compile(r"(\d+) automated tests"),
    re.compile(r"(\d+) автоматических тест\w*"),
)


def count_test_cases(root):
    """One case per [Fact], one per [InlineData] — which is what xUnit reports."""
    tests = os.path.join(root, "tests", "Gtamp.Tests")
    if not os.path.isdir(tests):
        return None

    total = 0
    for name in sorted(os.listdir(tests)):
        if not name.endswith(".cs"):
            continue
        with io.open(os.path.join(tests, name), encoding="utf-8") as handle:
            text = handle.read()
        total += text.count("[Fact]") + text.count("[InlineData")

    return total


def check_test_counts(root, files, problems):
    """A test count in the documentation must be the one the suite actually has."""
    expected = count_test_cases(root)
    if expected is None:
        problems.append("tests/Gtamp.Tests is missing — the test count check cannot work")
        return

    found_anywhere = False
    for path in files:
        with io.open(path, encoding="utf-8") as handle:
            text = handle.read()

        for pattern in TEST_COUNT_IN_DOC:
            for stated in pattern.findall(text):
                found_anywhere = True
                if int(stated) != expected:
                    problems.append(
                        "%s says %s tests; the suite has %d"
                        % (os.path.relpath(path, root).replace(os.sep, "/"), stated, expected))

    if not found_anywhere:
        problems.append(
            "no document states a test count any more — the check above cannot work")


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    files = list(markdown_files(root))
    problems = []

    check_links(root, files, problems)
    check_translation_pairs(root, problems)
    check_protocol_version(root, problems)
    check_test_counts(root, files, problems)

    print("checked %d markdown files" % len(files))
    for problem in problems:
        print("  BROKEN: %s" % problem)

    if problems:
        print("%d problem(s)" % len(problems))
        return 1
    print(
        "no broken links, no missing translations, "
        "protocol version and test count agree with the code")
    return 0


if __name__ == "__main__":
    sys.exit(main())
