#!/usr/bin/env python3
"""Bootstrap an owner and register a scratch project on an installed Task Server.

Credential values are read from protected files and never printed or persisted.
"""

import argparse
import json
from pathlib import Path
import secrets
import sys
from urllib import error, request


def post(url: str, bearer: str, path: str, payload: dict, session: str = "") -> dict:
    headers = {
        "Authorization": f"Bearer {bearer}",
        "X-Task-Protocol-Version": "2",
        "Content-Type": "application/json",
    }
    if session:
        headers["X-Studio-Session-Token"] = session
    call = request.Request(url.rstrip("/") + path, json.dumps(payload).encode(), headers, method="POST")
    try:
        with request.urlopen(call, timeout=20) as response:
            return json.load(response)
    except error.HTTPError as failure:
        raise RuntimeError(f"{path} returned HTTP {failure.code}") from None
    except error.URLError:
        raise RuntimeError(f"{path} could not reach the installed Task Server") from None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-url", required=True)
    parser.add_argument("--studio-token-file", type=Path, required=True)
    parser.add_argument("--owner-code-file", type=Path, required=True)
    parser.add_argument("--repository-url", required=True)
    args = parser.parse_args()
    try:
        bearer = args.studio_token_file.read_text().strip()
        code = args.owner_code_file.read_text().strip()
        if not bearer or not code:
            raise ValueError("installation credentials are empty")
        owner = post(args.server_url, bearer, "/api/v1/studio/auth/bootstrap", {
            "username": "scenario-owner", "password": secrets.token_urlsafe(30),
            "bootstrapCode": code,
        })
        session = owner["sessionToken"]
        workspace = post(args.server_url, bearer, "/api/v1/workspaces", {
            "name": "Installation scenario",
        }, session)
        project = post(args.server_url, bearer, "/api/v1/projects/registrations", {
            "projectId": "scenario-project", "workspaceId": workspace["workspaceId"],
            "name": "Scenario scratch", "taskKeyPrefix": "SCN",
            "repositoryUrl": args.repository_url, "integrationRef": "refs/heads/main",
            "deliveryPolicy": "reviewed-publication",
        }, session)
        if project["projectId"] != "scenario-project":
            raise RuntimeError("project registration returned a different project")
    except (OSError, ValueError, KeyError, RuntimeError) as failure:
        print(f"First-use bootstrap failed: {failure}", file=sys.stderr)
        return 1
    print("Owner bootstrapped and scratch project registered.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
