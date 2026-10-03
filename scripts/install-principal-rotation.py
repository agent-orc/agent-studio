#!/usr/bin/env python3
"""Install one Task Server bearer on its host and acknowledge scoped use.

Pipe the one-time management response to stdin. On retry, use --resume to read
the already installed protected file. This command prints no bearer or path.
"""

import argparse
import json
import os
import re
import stat
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, file_pointer, code, message, headers, new_url):
        return None


OPENER = urllib.request.build_opener(NoRedirect())


class TaskServerHttpError(RuntimeError):
    def __init__(self, status, code):
        super().__init__(f"Task Server returned HTTP {status} ({code}).")
        self.status = status
        self.code = code


def read_installed(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(descriptor, encoding="ascii") as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_mode & 0o037:
            raise ValueError("The credential file must be regular and inaccessible to other users.")
        value = source.read().strip()
    if not value:
        raise ValueError("The credential file is empty.")
    return value, info


def install(path, bearer):
    existing = None
    try:
        _, existing = read_installed(path)
    except FileNotFoundError:
        pass
    directory = os.path.dirname(os.path.abspath(path))
    descriptor, staged = tempfile.mkstemp(prefix=".principal-rotation-", dir=directory)
    try:
        mode = stat.S_IMODE(existing.st_mode) if existing else 0o600
        os.fchmod(descriptor, mode)
        if existing:
            os.fchown(descriptor, existing.st_uid, existing.st_gid)
        with os.fdopen(descriptor, "w", encoding="ascii") as target:
            descriptor = -1
            target.write(bearer + "\n")
            target.flush()
            os.fsync(target.fileno())
        os.replace(staged, path)
        folder = os.open(directory, os.O_RDONLY)
        try:
            os.fsync(folder)
        finally:
            os.close(folder)
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        if os.path.exists(staged):
            os.unlink(staged)


def require_installable(issued_receipt, current_receipt):
    if (current_receipt.get("credentialGeneration") != issued_receipt.get("credentialGeneration")
            or current_receipt.get("state") not in ("issued", "delivered", "awaiting-consumers")):
        raise RuntimeError("The rotation generation is no longer installable.")


def call(server, path, bearer, consumer, method="POST", body=None):
    data = json.dumps(body).encode("utf-8") if body is not None else (b"" if method == "POST" else None)
    request = urllib.request.Request(
        server + path,
        data=data,
        method=method,
        headers={
            "Authorization": "Bearer " + bearer,
            "X-Task-Protocol-Version": "2",
            "X-Principal-Consumer-Id": consumer,
            "Content-Type": "application/json",
        },
    )
    try:
        with OPENER.open(request, timeout=20) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        try:
            code = json.load(error).get("code", "request-failed")
        except (ValueError, AttributeError):
            code = "request-failed"
        raise TaskServerHttpError(error.code, code) from None


def wait_for_ack(send_ack, wait_seconds, now=time.monotonic, sleep=time.sleep):
    until = now() + wait_seconds
    while True:
        try:
            return send_ack()
        except TaskServerHttpError as error:
            if error.status != 409 or error.code != "rotation-scope-proof-required" or now() >= until:
                raise
            sleep(min(2, max(0, until - now())))


def old_bearer_rejected(server, operation, bearer, consumer, opener=OPENER):
    request = urllib.request.Request(
        server + "/api/v1/principal-rotations/" + urllib.parse.quote(operation, safe=""),
        headers={
            "Authorization": "Bearer " + bearer,
            "X-Task-Protocol-Version": "2",
            "X-Principal-Consumer-Id": consumer,
        },
    )
    try:
        with opener.open(request, timeout=20):
            return False
    except urllib.error.HTTPError as error:
        return error.code == 401


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", required=True)
    parser.add_argument("--operation-id", required=True)
    parser.add_argument("--consumer-id", required=True)
    parser.add_argument("--token-file", required=True)
    parser.add_argument("--wait-seconds", type=int, default=30)
    parser.add_argument("--resume", action="store_true")
    args = parser.parse_args()
    if args.wait_seconds < 0 or args.wait_seconds > 300:
        raise ValueError("Wait seconds must be between 0 and 300.")
    if not re.fullmatch(r"[A-Za-z0-9._:-]{1,128}", args.operation_id) or not re.fullmatch(
        r"[A-Za-z0-9._:-]{1,128}", args.consumer_id
    ):
        raise ValueError("Operation and consumer ids must use Task Server identifier characters.")
    parsed = urllib.parse.urlsplit(args.server)
    if parsed.scheme != "https" and not (parsed.scheme == "http" and parsed.hostname in ("localhost", "127.0.0.1", "::1")):
        raise ValueError("The Task Server URL must use HTTPS or loopback HTTP.")
    if parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ("", "/"):
        raise ValueError("The Task Server URL must be a bare origin without credentials or a query.")
    server = args.server.rstrip("/")
    backup = args.token_file + ".rotation-" + args.operation_id + ".previous"
    if args.resume:
        bearer, _ = read_installed(args.token_file)
    else:
        issued = json.load(sys.stdin)
        receipt = issued.get("rotation") or {}
        bearer = issued.get("credential")
        if receipt.get("operationId") != args.operation_id or not isinstance(bearer, str):
            raise ValueError("The one-time response does not match the requested operation.")
        if not any(item.get("consumerId") == args.consumer_id for item in receipt.get("consumers", [])):
            raise ValueError("The consumer is not declared in the rotation receipt.")
        if not bearer.startswith("ats_") or bearer.split(".", 1)[0][4:] != receipt.get("credentialGeneration"):
            raise ValueError("The bearer generation does not match the receipt.")
        current = call(server, "/api/v1/principal-rotations/" +
                       urllib.parse.quote(args.operation_id, safe=""),
                       bearer, args.consumer_id, method="GET")
        require_installable(receipt, current)
        if os.path.exists(backup):
            raise ValueError("A local rotation backup already exists; use --resume.")
        previous, _ = read_installed(args.token_file)
        install(backup, previous)
        install(args.token_file, bearer)
    operation = urllib.parse.quote(args.operation_id, safe="")
    call(server, f"/api/v1/principal-rotations/{operation}/delivered", bearer, args.consumer_id)
    result = wait_for_ack(
        lambda: call(server, f"/api/v1/principal-rotations/{operation}/ack", bearer,
                     args.consumer_id, body={"consumerId": args.consumer_id}),
        args.wait_seconds)
    if args.consumer_id not in result.get("acknowledgedConsumers", []):
        raise RuntimeError("Task Server did not acknowledge this consumer.")
    if result.get("state") == "retired":
        previous, _ = read_installed(backup)
        if not old_bearer_rejected(server, args.operation_id, previous, args.consumer_id):
            raise RuntimeError("Old bearer rejection was not verified.")
        os.unlink(backup)
    print(f"Rotation {args.operation_id}: {result['state']}")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, RuntimeError, urllib.error.URLError) as error:
        print(f"Principal rotation installation failed: {error}", file=sys.stderr)
        sys.exit(1)
