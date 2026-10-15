#!/usr/bin/env python3
"""Ask a Codex user to follow public installation docs in a disposable directory.

The agent receives only copied public docs and a candidate URL. The output is
friction evidence, not proof that the installed-stack scenario passed.
"""

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[2]
PUBLIC_DOCS = (
    Path("docs/operations/setup/install.md"),
    Path("docs/operations/setup/identity-and-project-bootstrap.md"),
    Path("docs/operations/setup/linux-runner-host.md"),
    Path("docs/operations/setup/multi-machine.md"),
)
SCHEMA = {
    "type": "object", "additionalProperties": False,
    "required": ["status", "attemptedSteps", "friction", "summary"],
    "properties": {
        "status": {"type": "string", "enum": ["completed", "blocked"]},
        "attemptedSteps": {"type": "array", "items": {"type": "string"}},
        "friction": {"type": "array", "items": {
            "type": "object", "additionalProperties": False,
            "required": ["location", "guess", "evidence"],
            "properties": {"location": {"type": "string"}, "guess": {"type": "string"},
                           "evidence": {"type": "string"}},
        }},
        "summary": {"type": "string"},
    },
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-url", required=True)
    parser.add_argument("--candidate-dir", type=Path)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--operator", action="store_true", help="Use the host's existing Codex login")
    parser.add_argument("--model", default="gpt-5.4-mini")
    args = parser.parse_args()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    if not args.operator and not os.environ.get("OPENAI_API_KEY"):
        args.report.write_text(json.dumps({"status": "skipped", "reason": "OPENAI_API_KEY is not configured",
                                           "attemptedSteps": [], "friction": []}, indent=2) + "\n")
        print("Simulated user skipped: OPENAI_API_KEY is not configured.")
        return 0
    executable = "codex" if args.operator else "docker"
    if shutil.which(executable) is None:
        print(f"{executable} is required for the simulated user.", file=sys.stderr)
        return 2
    if not args.operator and (args.candidate_dir is None or not args.candidate_dir.is_dir()):
        print("A candidate directory is required for the isolated CI user.", file=sys.stderr)
        return 2
    with tempfile.TemporaryDirectory(prefix="agent-studio-public-install-") as temporary:
        work = Path(temporary)
        for document in PUBLIC_DOCS:
            source = ROOT / document
            if not source.is_file():
                print(f"Public install document missing: {document}", file=sys.stderr)
                return 2
            target = work / document.name
            shutil.copy2(source, target)
        schema = work / "friction.schema.json"
        answer = work / "friction.json"
        schema.write_text(json.dumps(SCHEMA))
        release_url = args.release_url if args.operator else "file:///candidate/"
        prompt = (
            "You are a first-time user on a clean machine. You have only the public installation "
            "documents in this directory and this release URL: " + release_url + "\n"
            "Follow the documented Linux native installation and first-use steps as far as this "
            "environment permits. Actually run the documented commands when safe. Do not inspect "
            "source code, hidden files, or the parent checkout. Never display credentials or secret "
            "values. Record every place you had to guess, every missing prerequisite or instruction, "
            "and every command that failed. If you cannot complete a step, state the blocker and stop "
            "there. Return only the structured friction report."
        )
        if args.operator:
            command = ["codex", "exec", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config",
                       "--sandbox", "workspace-write", "--model", args.model,
                       "--config", "model_reasoning_effort=low",
                       "--output-schema", str(schema), "--output-last-message", str(answer), "-"]
        else:
            command = ["docker", "run", "--rm", "-i", "--cap-drop=ALL", "--network", "bridge",
                       "-e", "OPENAI_API_KEY", "-v", f"{work}:/work",
                       "-v", f"{args.candidate_dir.resolve()}:/candidate:ro", "-w", "/work",
                       "node:22-bookworm", "sh", "-c",
                       "npm install -g @openai/codex@0.155.0 >/dev/null 2>&1 && "
                       "exec codex exec --skip-git-repo-check --ephemeral --ignore-user-config "
                       "--dangerously-bypass-approvals-and-sandbox --model \"$1\" "
                       "--config model_reasoning_effort=low --output-schema /work/friction.schema.json "
                       "--output-last-message /work/friction.json -", "sh", args.model]
        try:
            with open(os.devnull, "w", encoding="utf-8") as discard:
                result = subprocess.run(command, input=prompt, text=True, cwd=work, stdout=discard,
                                        stderr=discard, timeout=900, check=False)
        except (OSError, subprocess.TimeoutExpired):
            print("Simulated user did not complete within 15 minutes.", file=sys.stderr)
            return 1
        if result.returncode or not answer.is_file():
            print(f"Simulated user failed with exit code {result.returncode}.", file=sys.stderr)
            return 1
        try:
            report = json.loads(answer.read_text())
            if report["status"] not in ("completed", "blocked") or not isinstance(report["friction"], list):
                raise ValueError("invalid friction report")
        except (ValueError, KeyError):
            print("Simulated user returned an invalid friction report.", file=sys.stderr)
            return 1
        # Credential values are outside the report schema, but scrub the CI key
        # in case the agent quoted its environment unexpectedly.
        serialized = json.dumps(report, indent=2)
        key = os.environ.get("OPENAI_API_KEY", "")
        if key:
            serialized = serialized.replace(key, "[redacted]")
        serialized = re.sub(r"(?i)\b[0-9a-f]{64}\b", "[redacted]", serialized)
        serialized = re.sub(r"aosj1[.][A-Za-z0-9._-]+", "[redacted]", serialized)
        serialized = re.sub(r"sk-[A-Za-z0-9_-]{16,}", "[redacted]", serialized)
        args.report.write_text(serialized + "\n")
        print(f"Simulated user: {report['status']}; friction items: {len(report['friction'])}.")
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
