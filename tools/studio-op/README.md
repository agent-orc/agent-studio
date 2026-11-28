# studio-op

The operator CLI of Agent Studio: `login`, `watch`, `triage`, `continue`, `move`,
`accept`, `create`, `title`, `capacity`, `backups`, `security`, `routing`,
`preflight`, `deploy plan`, `log` and `magazine publish` over the documented operator REST routes. Node 22, no dependencies.

```sh
npm install --global ./tools/studio-op   # or: node tools/studio-op/studio-op.mjs
studio-op auth status
```

The token is read from `--token-file`, `STUDIO_OP_TOKEN` (also
`AGENT_STUDIO_API_TOKEN`), a config `tokenFile`, or the default
`~/.config/agent-studio/operator-token` (mode 600). Existing login sessions use
`operator-session`, which `studio-op login` writes after the Owner confirms
the terminal's code in Studio with a second factor. On Unix, the resident signer
creates a private socket directory under `XDG_RUNTIME_DIR`, the system temp
directory, or `/tmp`, in that order. It checks the complete endpoint's byte
length before binding. The socket has mode 0600 and its directory has mode 0700.
Setup, the command-to-route table and the stable exit codes:
[docs/operations/setup/studio-op.md](../../docs/operations/setup/studio-op.md).
Board scripts use `snap`, `card`, `ready`, `deps`, `continue`, `move`, `top`,
`route`, `depends`, `accept`, `accept-with-reason`, `create FILE`, `quota`,
`why`, `watch`, `get`, `post` and `put`. Each supports `--json`; `watch` emits
NDJSON. Board API refusals return 4, missing cards 3, success 0. The existing
decision-event watcher is `watch --decisions`.

Runbooks for agent sessions: [.agents/skills/operator](../../.agents/skills/operator/SKILL.md).

Tests: `node --test tools/studio-op/test/*.test.mjs` (unit and fake-Studio),
`frontend/e2e/operator/studio-op-operator-cli.spec.ts` (prebuilt networked stack) and
`frontend/e2e/security/terminal-sign-in.networked-https.spec.ts` (login on the networked-https stack).

`studio-op routing drill --dry-run --project <project> --json` reads the installation routing drill. It uses fake quotas, invokes no model, writes no routing receipts and lists missing Token Economy equivalents. `studio-op routing` also shows the short-step quota reserve.
