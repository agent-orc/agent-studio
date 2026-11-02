#!/usr/bin/env python3
"""Refresh workflow action SHAs and Docker base image digests from upstream."""

import pathlib
import re
import subprocess
import sys


ROOT = pathlib.Path(__file__).resolve().parents[2]


def command(*args):
    return subprocess.check_output(args, text=True, timeout=90)


def action_sha(name, tag):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*", name):
        raise ValueError("Invalid action name")
    repository = "/".join(name.split("/")[:2])
    lines = command("git", "ls-remote", f"https://github.com/{repository}.git",
                    f"refs/tags/{tag}", f"refs/tags/{tag}^{{}}").splitlines()
    peeled = next((line for line in lines if line.endswith("^{}")), None)
    match = peeled or next((line for line in lines if line.endswith("/" + tag)), None)
    if match is None:
        raise ValueError(f"Missing action tag {name}@{tag}")
    return match.split()[0]


def image_digest(ref):
    output = command("docker", "buildx", "imagetools", "inspect", ref)
    match = re.search(r"^Digest:\s*(sha256:[a-f0-9]{64})$", output, re.M)
    if match is None:
        raise ValueError(f"Missing image digest for {ref}")
    return match.group(1)


def refresh(path):
    content = path.read_text()

    def action(match):
        name, _old, tag = match.groups()
        return f"uses: {name}@{action_sha(name, tag)} # {tag}"

    content = re.sub(r"uses:\s+([\w.-]+/[\w.-]+(?:/[\w.-]+)*)@([a-f0-9]{40})\s+#\s+(v[\d.]+)", action, content)

    def image(match):
        ref = match.group(1)
        return ref + "@" + image_digest(ref)

    content = re.sub(r"([\w./-]+:[\w.-]+)@sha256:[a-f0-9]{64}", image, content)
    if content != path.read_text():
        path.write_text(content)
        print(path.relative_to(ROOT))


def main():
    paths = list((ROOT / ".github" / "workflows").glob("*.yml"))
    paths += [p for p in ROOT.rglob("Dockerfile") if not any(x in p.parts for x in ("node_modules", "obj", "bin", ".git"))]
    paths += [p for p in ROOT.rglob("*.Dockerfile") if not any(x in p.parts for x in ("node_modules", "obj", "bin", ".git"))]
    for path in sorted(set(paths)):
        refresh(path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
