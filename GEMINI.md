# Gemini CLI Compatibility

Read [AGENTS.md](AGENTS.md) first; it is the root navigation card for this repository.
Frontend-specific rules live in [frontend/AGENTS.md](frontend/AGENTS.md).

## Verification

- Run builds and tests in the foreground with a timeout appropriate to the
  command, or start them in the background and use one blocking command to wait
  (for example, `wait <pid>` in the launching shell or
  `timeout 20m tail --pid=<pid> -f /dev/null`), then read the end of the log once.
- Do not poll logs in a loop of separate commands. If a command can outlast the
  tool's per-call limit, wait in blocks of at least five minutes.
- Each check re-sends the whole conversation, and builds on the shared host take
  minutes.
