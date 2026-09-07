# Contributing

Thank you for helping improve Agent Studio. Bug reports, feature ideas,
documentation fixes, and code contributions are welcome.

## Before you start

- Use GitHub Issues for reproducible bugs and focused feature proposals.
- Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).
- For a substantial change, open an issue first so scope and product fit can be
  agreed before implementation.

## Local setup

The tested development baseline is Windows with Git Bash, the .NET 10 SDK,
Node.js 22, and npm 11. Follow the
[getting-started guide](docs/operations/setup/getting-started.md) for local
configuration and startup.

Install dependencies and run the main checks from the repository root:

```bash
dotnet restore agent-taskboard.sln
dotnet build agent-taskboard.sln --configuration Release --no-restore
dotnet test agent-taskboard.sln \
  --configuration Release \
  --no-build \
  --filter "Category!=MachineBound"

npm ci --prefix frontend
npm --prefix frontend run lint:ci
npm --prefix frontend run typecheck
npm --prefix frontend run test:ci
npm --prefix frontend run build

bash scripts/release/release-scripts.test.sh
```

Changes to the deployed topology, the Task Server, the Studio BFF, the runner,
the installer, or the Compose stack also run the deployment regression scenario.
It is the one end-to-end run every deployment change passes, it needs no Docker
at the `inproc` target, and the smoke level finishes in seconds:

```bash
bash scripts/scenario.sh --target inproc --level smoke
```

See [deployment regression scenario](docs/operations/testing/deployment-scenario.md)
for the other targets, the report format, and how to add a step.

Frontend behavior changes also require relevant Playwright coverage. See
[frontend/e2e/README.md](frontend/e2e/README.md) for the test setup and
evidence conventions.

## Coding conventions

Read [AGENTS.md](AGENTS.md) before changing the repository. It contains the
mandatory repository guardrails and links to the domain documentation. Changes
under `frontend/` also follow [frontend/AGENTS.md](frontend/AGENTS.md).
Technology-specific conventions are indexed in the
[contribution and style guide](docs/start/contribution-and-style-guide.html).

Keep changes focused, update tests with behavior changes, and update public
documentation or the changelog when users need to know about the change.

## Pull requests and agent-driven work

The maintainers normally run a 0-PR, agent-driven pipeline in managed
worktrees. That is an internal delivery workflow, not a restriction on
community participation. Human-authored pull requests are welcome and receive
the same evidence-based review.

For a pull request:

1. Fork the repository and create a focused branch.
2. Add or update tests in proportion to the change.
3. Run the relevant checks above.
4. Explain the problem, the chosen solution, and the verification evidence.
5. Keep unrelated refactors out of the same pull request.

By contributing, you agree that your contribution is licensed under the
repository's [Apache License 2.0](LICENSE).
