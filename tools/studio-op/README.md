# studio-op

The operator CLI of Agent Studio: `watch`, `triage`, `continue`, `move`,
`accept`, `create`, `title`, `capacity`, `backups`, `security`, `routing`,
`preflight`, `deploy plan` and `log` over the documented operator REST routes. Node 22, no dependencies.

```sh
npm install --global ./tools/studio-op   # or: node tools/studio-op/studio-op.mjs
studio-op auth status
```

The token is read only from `~/.config/agent-studio/operator-token` (mode 600).
Setup, the command-to-route table and the stable exit codes:
[docs/operations/setup/studio-op.md](../../docs/operations/setup/studio-op.md).
Runbooks for agent sessions: [.agents/skills/operator](../../.agents/skills/operator/SKILL.md).

Tests: `node --test tools/studio-op/test/*.test.mjs` (unit and fake-Studio) and
`frontend/e2e/operator/studio-op-operator-cli.spec.ts` (prebuilt networked stack).
