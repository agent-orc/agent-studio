#!/usr/bin/env python3
"""Rotate one Compose principal through the Task Server management API."""
import json
import os
import sys
import tempfile
from urllib.parse import quote
from urllib.request import Request, urlopen

PRINCIPALS = {
    "studio": ("bootstrap-studio", "studio_token"),
    "engine": ("bootstrap-engine", "engine_token"),
    "runner": ("runner:distributed-runner", "runner_token"),
    "coding": ("runner:compose-coding", "coding_runner_token"),
    "review": ("runner:compose-review", "review_runner_token"),
}


def main() -> int:
    if len(sys.argv) != 2 or sys.argv[1] not in PRINCIPALS:
        print("usage: credential-manager <studio|engine|runner|coding|review>", file=sys.stderr)
        return 64
    name = sys.argv[1]
    principal_id, filename = PRINCIPALS[name]
    directory = "/run/secrets"
    target = os.path.join(directory, filename)
    with open(os.path.join(directory, "studio_token"), encoding="ascii") as source:
        admin_token = source.read().strip()
    if not os.access(directory, os.W_OK) or not os.path.isfile(target):
        print(f"credential volume is not writable or {filename} is missing", file=sys.stderr)
        return 1

    server_url = os.environ.get("ROTATION_TASK_SERVER_URL", "http://task-server:5071").rstrip("/")
    request = Request(
        server_url + "/api/v1/management/principals/"
        + quote(principal_id, safe="") + "/rotate",
        data=b"{}",
        headers={
            "Authorization": f"Bearer {admin_token}",
            "X-Task-Protocol-Version": "2",
            "Content-Type": "application/json",
        },
        method="POST",
    )
    with urlopen(request, timeout=30) as response:
        credential = json.load(response)["credential"]
    if not isinstance(credential, str) or len(credential) < 32:
        raise ValueError("Task Server returned an invalid credential")

    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="ascii", dir=directory, prefix=f".{filename}.", delete=False
        ) as output:
            temporary = output.name
            os.fchmod(output.fileno(), 0o600)
            os.fchown(output.fileno(), 10001, 10001)
            output.write(credential)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, target)
    finally:
        if temporary and os.path.exists(temporary):
            os.unlink(temporary)
    print(f"rotated {name} principal; restart its client to load the new file")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"credential rotation failed: {error}", file=sys.stderr)
        sys.exit(1)
