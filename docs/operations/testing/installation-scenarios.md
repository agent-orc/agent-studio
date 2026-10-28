# Installation scenario evidence

`scripts/release/installation-scenarios.py` verifies every candidate asset in
`SHA256SUMS`, runs a named probe, and writes `<scenario>.json` and
`<scenario>.html`. The driver discards probe output so credentials do not enter
those evidence files. A failed probe exits nonzero. `not_applicable` and
`skipped` remain visible and never mean that a journey was proven. Required
installed first-use checks have no deferred allowance. An unimplemented step
fails the release gate. Optional operator and simulated-user skips remain
visible without hiding deterministic failures.

The release workflow runs Linux native, Linux Docker, coding and review,
Windows native, and Debian, Ubuntu and Fedora container probes after packaging.
It gates publication on those jobs. The published-image upgrade probe runs
when a previous GitHub Release exists; the first release records
`not_applicable`. The nightly workflow builds candidate assets at the checked
out revision and runs the same probes. Nightly Docker images are built locally
and the installer runs in offline mode.

The native probe is `setup-e2e-probe.sh` with `SETUP_E2E_RELEASE_DIR`. It serves
the candidate release on loopback and runs its `install.sh`, then uses real
candidate binaries, a fake `systemctl`, a fake Codex CLI, and a join
token held in a private temporary file. Its default remote is a temporary bare
repository served by `git daemon` on loopback. The daemon and repository are
removed afterward. The probe checks Git read and startup push capability,
bootstraps the owner with the installer's one-time code, and registers the
scratch repository as a project. It then creates a ready card, freezes a
deterministic review plan, runs the fake CLI from the installed host, and
starts installed review and integration workers. The final assertion requires
a passing review, an integrated command and the published Git SHA. Loopback
`git://` is accepted for this local journey; other cleartext Git origins remain
rejected.

The optional GitHub access probe needs the `AGENT_STUDIO_SCENARIO_DEPLOY_KEY` and
`AGENT_STUDIO_SCENARIO_KNOWN_HOSTS` secrets plus the
`AGENT_STUDIO_SCENARIO_GIT_REMOTE` repository variable. They must be supplied
together. It checks read access and a dry-run push separately; the required
installed card uses only the loopback fixture. The default release and nightly
gates need none of them.

The Docker probe installs the verified Compose bundle, bootstraps the owner,
registers a scratch Git remote and drives a card through installed candidate
coding, review and integration containers. It uninstalls without purge,
reinstalls, and checks that the installation ID, workspace and card remain.
The separate coding and review probe calls
`scripts/scenario.sh --target compose --level full`, using the disposable
repository and fake CLI in `testsupport/scenario`. That probe builds scenario
images from the checkout and reaches human review with a supervised fixture
publication. It is separate evidence from the installed candidate topology.
`compose-upgrade-test.sh` checks that a previous published image store survives
an image update when both versions exist.

The simulated user runs `simulated-install-user.py` in a disposable container
with only copies of public setup documents and read-only candidate assets
mounted. It asks Codex to follow documented Linux native steps as far as the
container allows
and writes a structured friction report naming guesses and blockers. CI uses
`gpt-5.4-mini` when `AGENT_STUDIO_SIMULATED_USER_API_KEY` is configured.
Otherwise the scenario JSON and HTML show `skipped`. This diagnostic report
is separate from deterministic pass or fail evidence.

For an operator run on `agent-runner-01`, stage Linux candidate assets and run
`python3 scripts/release/installation-scenarios.py --scenario linux-native
--candidate-dir PATH --report-dir PATH`. Run `--scenario linux-docker` for the
one-box Compose journey and `--scenario simulated-user --operator-profile` to
use the host's existing Codex login. On KORI, stage the Windows assets and
run the driver with `--scenario windows-native`. For the cross-host connector,
run `--scenario windows-linux-host --operator-profile --server-url URL --token-file PATH` on KORI
with a protected Studio token file for the Linux Task Server. The URL must be
reachable over trusted HTTPS. The probe requires `agent-runner-01` in the remote
host registry and a board response through the installed Windows connector.
Store reports outside the worktree if they must
survive cleanup. Docker and Windows native probes require disposable clean
hosts and refuse an existing installation. On a host with KVM,
`--scenario linux-vm` calls `compose-smoke-vm-test.sh` and stores VM evidence.
That VM script boots a source archive, so it does not prove candidate artifact
installation.

The Windows native probe uses a disposable loopback Git daemon and a compiled
fake Codex CLI. It starts coding, review and integration roles from the
installed `agent-host.exe`, checks the published SHA and verifies that the
card survives a retained-data reinstall. The separate source-built coding
fixture stops at human review. The broken-prerequisites scenario checks missing Git and records
port, provider login, and GitHub access as skipped with reasons. The
Windows-to-Linux-host scenario needs an operator endpoint and token. The
operator simulated-user profile uses the host's existing login and its prompt
limits reads to copied public docs. These gaps must be closed before
the entire first-use journey can be called green.
