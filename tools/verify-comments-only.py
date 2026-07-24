#!/usr/bin/env python3
"""verify-comments-only.py -- prove that a range of commits changed comments only.

Tokenizes each changed C# file before and after, strips comments and normalizes whitespace, then
compares. Identical stripped forms mean no executable statement changed. String and character
literals (including verbatim @"..." strings) are preserved, so a comment-looking sequence inside a
string is never mistaken for a comment.

Usage (run from the repository root):
    python tools/verify-comments-only.py [<git-range>]

    <git-range> defaults to "v0.8.32..HEAD" -- the documentation and comment work sitting on top of
    the tagged build this package ships. Also accepts A..B, A...B (compares from the merge base) and
    a bare revision (compares that revision against the current working tree). Both sides of a
    two-dot or three-dot range are read from git, never from the working tree.

Exit code 0 = comments only; 1 = at least one file's executable content differs. This is the check
cited by HANDOFF-MANIFEST.md and by the handoff package's README.
"""
import io
import subprocess
import sys

BS = chr(92)


def strip(src):
    """Remove comments and collapse whitespace, preserving string/char literals."""
    out, i, n = [], 0, len(src)
    mode = 'code'  # code | line | block | str | verbatim | char
    while i < n:
        c = src[i]
        nxt = src[i + 1] if i + 1 < n else ''
        if mode == 'code':
            if c == '/' and nxt == '/':
                mode = 'line'; i += 2; continue
            if c == '/' and nxt == '*':
                mode = 'block'; i += 2; continue
            if c == '@' and nxt == '"':
                mode = 'verbatim'; out.append('@"'); i += 2; continue
            if c == '"':
                mode = 'str'; out.append(c); i += 1; continue
            if c == "'":
                mode = 'char'; out.append(c); i += 1; continue
            out.append(c); i += 1; continue
        if mode == 'line':
            if c == '\n':
                mode = 'code'; out.append(c)
            i += 1; continue
        if mode == 'block':
            if c == '*' and nxt == '/':
                mode = 'code'; i += 2; continue
            i += 1; continue
        if mode == 'str':
            out.append(c)
            if c == BS:
                out.append(nxt); i += 2; continue
            if c == '"':
                mode = 'code'
            i += 1; continue
        if mode == 'verbatim':
            out.append(c)
            if c == '"':
                if nxt == '"':
                    out.append(nxt); i += 2; continue
                mode = 'code'
            i += 1; continue
        if mode == 'char':
            out.append(c)
            if c == BS:
                out.append(nxt); i += 2; continue
            if c == "'":
                mode = 'code'
            i += 1; continue
    return ' '.join(''.join(out).split())


def endpoints(rng):
    """Resolve a range to (base, tip). tip None means 'the working tree'.

    Both sides must be resolved explicitly: reading the 'after' side from disk when the range names
    an explicit right-hand commit would compare the wrong pair and could report a real code change
    as comments-only.
    """
    if "..." in rng:                       # A...B -> compare from the merge base
        a, _, b = rng.partition("...")
        a, b = a or "HEAD", b or "HEAD"
        base = subprocess.check_output(['git', 'merge-base', a, b], text=True).strip()
        return base, b
    if ".." in rng:                        # A..B (B defaults to HEAD)
        a, _, b = rng.partition("..")
        return (a or "HEAD"), (b or "HEAD")
    return rng, None                       # bare rev: git diff <rev> compares against the work tree


def blob(ref, path):
    """File content at <ref>, or from the working tree when ref is None."""
    if ref is None:
        return io.open(path, encoding='utf-8-sig', errors='replace').read()
    out = subprocess.check_output(['git', 'show', ref + ':' + path], text=False)
    return out.decode('utf-8-sig', errors='replace')


def main(argv):
    rng = argv[1] if len(argv) > 1 else "v0.8.32..HEAD"
    try:
        changed = subprocess.check_output(['git', 'diff', '--name-only', rng], text=True).split()
        base, tip = endpoints(rng)
    except subprocess.CalledProcessError as e:
        print("git failed for range %r: %s" % (rng, e))
        return 2
    print("comparing %s -> %s" % (base, tip if tip is not None else "working tree"))
    cs = [f for f in changed if f.endswith('.cs')]
    if not cs:
        print("no .cs files changed in %s -- nothing to prove" % rng)
        return 0
    bad = []
    for f in cs:
        try:
            before = blob(base, f)
        except subprocess.CalledProcessError:
            print("  %-13s %s" % ("ADDED", f))
            bad.append(f)
            continue
        try:
            after = blob(tip, f)
        except (subprocess.CalledProcessError, IOError):
            print("  %-13s %s" % ("DELETED", f))
            bad.append(f)
            continue
        same = strip(before) == strip(after)
        print("  %-13s %s" % ("comments-only" if same else "DIFFERS", f))
        if not same:
            bad.append(f)
    print("\nfiles compared: %d" % len(cs))
    if bad:
        print("FAIL: executable content changed in: %s" % ", ".join(bad))
        return 1
    print("PASS: comment-stripped content is identical for every changed .cs file in %s" % rng)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
