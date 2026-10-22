#!/usr/bin/env python3
"""Post a passed operator verification summary to Agent Studio."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("summary", type=Path, help="JSON summary from the verification host")
    parser.add_argument("--url", required=True, help="Studio base URL")
    parser.add_argument("--project", required=True, help="Project ID or short code")
    args = parser.parse_args()

    token = os.environ.get("STUDIO_OPERATOR_TOKEN")
    if not token:
        parser.error("STUDIO_OPERATOR_TOKEN must hold an Operator workspace token")
    summary = json.loads(args.summary.read_text(encoding="utf-8"))
    log = Path(summary.pop("logFile"))
    if not log.is_absolute():
        log = args.summary.parent / log
    digest = hashlib.sha256()
    with log.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    summary["logHash"] = digest.hexdigest()
    path = f"/api/projects/{quote(args.project, safe='')}/operator-candidate-verifications"
    request = Request(
        args.url.rstrip("/") + path,
        data=json.dumps(summary).encode("utf-8"),
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urlopen(request, timeout=30) as response:
            result = json.load(response)
    except (HTTPError, URLError) as error:
        if isinstance(error, HTTPError):
            print(error.read().decode("utf-8", errors="replace"), file=sys.stderr)
        else:
            print(error, file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2))
    return 0 if all(item["recorded"] for item in result["results"]) else 2


if __name__ == "__main__":
    sys.exit(main())
