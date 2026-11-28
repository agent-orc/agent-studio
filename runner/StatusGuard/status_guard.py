"""Attempt-scoped Bash status throttling. Command strings are data and never executed."""

import hashlib
import json
import math
import os
import re
import shutil
import sqlite3
import sys
import time


TOOLS = frozenset("tail head cat rg grep ls test [ ps pgrep date wc pwd".split())
BUILTINS = frozenset(("test", "[", "pwd"))
OPERATORS = frozenset((";", "\n", "|", "&&", "||"))
# Keep quoted text and shell operators distinct. Exclude shell expansion syntax
# except for plain, quoted paths rooted at a worker-provided absolute directory.
WORD = r"""(?:[a-zA-Z0-9_./,:=+@%!?*\[\]~-]+|'[^']*'|"[^"`\\]*")+"""
LEX = re.compile(r"[ \t]+|&&|\|\||[;\n|]|" + WORD)
QUOTED_PATH = re.compile(r'^"\$(?:JOB_RESULTS_DIR|PWD)/[^"`$\\]*"$')
WORD_PART = re.compile(r"""'([^']*)'|"([^"]*)"|([^'"]+)""")

SHORT = {
    "tail": "nqc v".replace(" ", ""),
    "head": "nqcv",
    "rg": "nHiSsiwFxvlLcqohA B C e M m g t T u a z N U 0".replace(" ", ""),
    "grep": "nHiwFxvlLcqohA B C e m r R s E G P a I Z".replace(" ", ""),
    "date": "uRIdr",
}
LONG = {
    "tail": "lines bytes quiet silent verbose zero-terminated",
    "head": "lines bytes quiet silent verbose zero-terminated",
    "rg": """
        files files-with-matches files-without-match count count-matches line-number
        no-line-number with-filename no-filename ignore-case case-sensitive smart-case
        fixed-strings word-regexp line-regexp invert-match only-matching quiet heading
        no-heading color colors context before-context after-context glob iglob type
        type-not hidden no-ignore no-ignore-vcs no-ignore-parent no-ignore-global no-ignore-dot
        max-count max-columns max-columns-preview max-depth max-filesize regexp file json
        sort sortr no-config binary text multiline multiline-dotall null null-data
    """,
    "grep": """
        extended-regexp fixed-strings basic-regexp perl-regexp regexp file ignore-case
        invert-match word-regexp line-regexp count color colour files-with-matches
        files-without-match only-matching line-number with-filename no-filename quiet
        silent no-messages before-context after-context context max-count recursive
        dereference-recursive include exclude exclude-dir binary-files text null null-data
    """,
    "date": "utc universal rfc-email iso-8601 rfc-3339 date reference",
}
SHORT_VALUE = {"tail": "nc", "head": "nc", "rg": "ABCegmMtT", "grep": "ABCem", "date": "Idr"}


def allowed_arguments(tool, args):
    if tool not in SHORT:
        return True
    for arg in args:
        if arg.startswith("--"):
            if arg[2:].split("=", 1)[0] not in LONG[tool].split():
                return False
        elif tool in ("head", "tail") and re.fullmatch(r"-[0-9]+", arg):
            continue
        elif arg.startswith("-"):
            for flag in arg[1:]:
                if flag not in SHORT[tool]:
                    return False
                if flag in SHORT_VALUE[tool]:
                    break
    return True


def status_tools(command):
    """Return the allowlisted commands in a simple status pipeline or list."""
    stages = []
    words = []
    end = 0
    need_stage = False
    for token in LEX.finditer(command):
        if token.start() != end:
            return None
        end = token.end()
        word = token.group()
        if not word.strip(" \t"):
            continue
        if word in OPERATORS:
            if words:
                stages.append(words)
                words = []
                need_stage = word in ("|", "&&", "||")
            elif word != "\n":
                return None
            continue
        if not words:
            words.append(word)
            need_stage = False
        elif QUOTED_PATH.fullmatch(word):
            variable = word[2:word.index("/")]
            if not os.environ.get(variable, "").startswith("/"):
                return None
            words.append("/path")
        else:
            parts = list(WORD_PART.finditer(word))
            if any(p[2] is not None and "$" in p[2] for p in parts):
                return None
            if words[0] in SHORT and any(p[3] and re.search(r"[*?\[]", p[3]) for p in parts):
                # A bare glob can expand to an option such as rg's --pre.
                if not word.startswith(("/", "./")):
                    return None
            words.append("".join(next(v for v in p.groups() if v is not None) for p in parts))
    if words:
        stages.append(words)
    if end != len(command) or need_stage or not stages:
        return None
    if any(words[0] not in TOOLS or not allowed_arguments(words[0], words[1:]) for words in stages):
        return None
    return {words[0] for words in stages}


def trusted_tools(shell_types):
    trusted = set()
    for line in shell_types.splitlines():
        fields = line.split(" ", 1)
        if len(fields) != 2:
            continue
        name, path = fields
        if (path == "builtin" and name in BUILTINS
                or path in ("/bin/" + name, "/usr/bin/" + name)
                or name == "rg" and re.fullmatch(r"/opt/agent-cli/releases/[^ ]+/codex-path/rg", path)):
            trusted.add(name)
    return trusted


def delay_for(previous, now, interval, cap):
    remaining = 0 if previous is None else max(0, previous + interval - now)
    return min(remaining, cap), remaining > cap


def record(database, command_key, interval, cap):
    # Serialize reservations briefly, then release the transaction before sleeping.
    # A concurrently started copy sees the reserved execution time.
    with sqlite3.connect(database, timeout=0.2, isolation_level="IMMEDIATE") as connection:
        connection.execute("BEGIN IMMEDIATE")
        connection.execute("CREATE TABLE IF NOT EXISTS status (key BLOB PRIMARY KEY, last REAL)")
        connection.execute("CREATE TABLE IF NOT EXISTS calls (repeated INTEGER, delayed INTEGER)")
        now = time.monotonic()
        delay, capped = 0, False
        if command_key is not None:
            row = connection.execute("SELECT last FROM status WHERE key = ?", (command_key,)).fetchone()
            delay, capped = delay_for(None if row is None else row[0], now, interval, cap)
            connection.execute("INSERT OR REPLACE INTO status VALUES (?, ?)", (command_key, now + delay))
        repeated = delay > 0 or capped
        connection.execute("INSERT INTO calls VALUES (?, ?)", (repeated, delay > 0))
    return delay, capped


def main(database, interval, cap, command):
    return guard(database, int(interval), int(cap), command, os.getcwd(), trusted_tools(sys.stdin.read()))


def guard(database, interval, cap, command, cwd, trusted, cap_name="command tool"):
    tools = status_tools(command)
    key = None
    if tools and tools <= trusted and not (
            "rg" in tools and os.environ.get("RIPGREP_CONFIG_PATH")
            or "grep" in tools and os.environ.get("GREP_OPTIONS")):
        key = hashlib.sha256(os.fsencode(cwd) + b"\0" + os.fsencode(command)).digest()
    delay, capped = record(database, key, interval, cap)
    if delay > 0:
        time.sleep(delay)
        limit = f" (wait capped for the {cap_name} timeout)" if capped else ""
        return f"waited {math.ceil(delay)} s{limit}: this status command repeats; wait for the process instead (see AGENTS.md)"
    return ""


def claude_hook(database, interval, cap):
    event = json.load(sys.stdin)
    if event.get("hook_event_name") != "PreToolUse" or event.get("tool_name") != "Bash":
        return
    command = event.get("tool_input", {}).get("command")
    cwd = event.get("cwd")
    if not isinstance(command, str) or not isinstance(cwd, str):
        return
    shell_types = "\n".join(
        name + " " + ("builtin" if name in BUILTINS else (shutil.which(name) or ""))
        for name in TOOLS
    )
    notice = guard(database, int(interval), int(cap), command, cwd, trusted_tools(shell_types), "provider hook")
    if notice:
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "additionalContext": notice}}))


if __name__ == "__main__":
    try:
        if len(sys.argv) == 3 and sys.argv[1] == "summarize":
            with sqlite3.connect(sys.argv[2]) as connection:
                print(*connection.execute(
                    "SELECT COUNT(*), COALESCE(SUM(delayed), 0), COALESCE(SUM(repeated), 0) FROM calls"
                ).fetchone())
        elif len(sys.argv) == 5 and sys.argv[1] == "claude-hook":
            claude_hook(*sys.argv[2:])
        else:
            notice = main(*sys.argv[1:])
            if notice:
                print(notice)
    except (OSError, sqlite3.Error, ValueError):
        # Telemetry is best effort. The original command still runs.
        sys.exit(1)
