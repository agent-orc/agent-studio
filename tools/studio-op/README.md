# studio-op

The operator CLI of Agent Studio: `login`, `watch`, `triage`, `continue`, `move`,
`accept`, `create`, `title`, `capacity`, `backups`, `security`, `routing`,
`preflight`, `deploy plan`, `log` and `magazine publish` over the documented operator REST routes. Node 22, no dependencies.

```sh
npm install --global ./tools/studio-op   # or: node tools/studio-op/studio-op.mjs
studio-op auth status
```

The token is read only from `~/.config/agent-studio/operator-token` (mode 600),
or `operator-session`, which `studio-op login` writes after the Owner confirms
the terminal's code in Studio with a second factor.
Setup, the command-to-route table and the stable exit codes:
[docs/operations/setup/studio-op.md](../../docs/operations/setup/studio-op.md).
Runbooks for agent sessions: [.agents/skills/operator](../../.agents/skills/operator/SKILL.md).

Tests: `node --test tools/studio-op/test/*.test.mjs` (unit and fake-Studio),
`frontend/e2e/operator/studio-op-operator-cli.spec.ts` (prebuilt networked stack) and
`frontend/e2e/security/terminal-sign-in.networked-https.spec.ts` (login on the networked-https stack).

`studio-op routing drill --dry-run --project <project> --json` reads the installation routing drill. It uses fake quotas, invokes no model, writes no routing receipts and lists missing Token Economy equivalents. `studio-op routing` also shows the short-step quota reserve.
