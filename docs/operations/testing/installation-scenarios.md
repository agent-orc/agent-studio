# Installation scenario evidence

`scripts/release/installation-scenarios.py` runs a named scenario against a
release-shaped candidate directory. It verifies every file in `SHA256SUMS`
before starting a probe and writes `<scenario>.json` plus `<scenario>.html` to
the report directory. The driver discards command output so tokens and keys do
not enter the evidence artifact. A nonzero probe exit makes the scenario fail.

The release workflow runs the Linux native, Linux Docker, coding and review,
Windows native, and Debian, Ubuntu and Fedora container probes after packaging.
The published-image upgrade probe runs when a previous GitHub Release exists;
the first release records `not_applicable` with the reason in its report.
The release publication job needs every installation job to pass. The nightly
workflow builds candidate assets at the checked-out revision and runs the same
native, Docker, coding and review, and Windows probes. Nightly Docker images
are built locally and the installer runs in offline mode.

The native probe is `setup-e2e-probe.sh` with `SETUP_E2E_RELEASE_DIR`. It uses
real candidate binaries, a fake `systemctl`, a fake Codex login, a join token
held in a private temporary file, and Git read and write capability probes. In CI, Git uses
the scratch GitHub repository named by `INSTALL_SCENARIO_GIT_REMOTE` and an
SSH deploy key with write access from `INSTALL_SCENARIO_DEPLOY_KEY`; the pinned SSH host entry
comes from `INSTALL_SCENARIO_KNOWN_HOSTS`. Those three repository secrets are
required by the release and nightly jobs. The probe removes the key and join
token when it exits. Do not put provider credentials in those jobs.

The Docker probe installs the verified Compose bundle, creates one API
workspace, uninstalls without purge, reinstalls, and checks both the
installation ID and workspace still exist. The coding and review probe calls
`scripts/scenario.sh --target compose --level full`, which uses the disposable
repository and fake CLI in `testsupport/scenario`. That probe builds scenario
images from the checkout and currently reaches human review with a supervised
fixture publication. It is separate evidence from installation of candidate
images. `compose-upgrade-test.sh` proves a previous published image store
survives a switch to the candidate image tag when both image versions exist.

For an operator run on `agent-runner-01`, stage the Linux candidate assets and
run `python3 scripts/release/installation-scenarios.py --scenario linux-native
--candidate-dir PATH --report-dir PATH`. On KORI, stage the Windows candidate
assets and run the same driver with `--scenario windows-native`. For the
cross-host connector, run `--scenario windows-linux-host --server-url URL
--token-file PATH` on KORI with a protected Studio token file for the Linux
Task Server. The cross-host URL must be reachable over trusted HTTPS. Store
reports outside the repository if they must survive worktree cleanup.
Run Docker and Windows native scenarios only on disposable clean hosts. Their
probes refuse an existing Compose project or scheduled tasks before installing.
An operator host with KVM can also run `--scenario linux-vm`; it calls
`compose-smoke-vm-test.sh` and places the VM serial evidence below the report
directory. That VM script boots a source archive, so its report does not prove
candidate artifact installation.

Current limits: the native probe verifies Git read and startup write capability,
not a completed deploy-key push or a complete owner bootstrap. The coding fixture is not the installed
candidate topology and stops at human review because Task Server has no native
integration transition in that topology. The simulated AI user, clean-host
prerequisite matrix, and cross-host operator run require separate execution
evidence before the whole first-use goal can be claimed. The scenario reports
do not mark any of these untested steps as passed.
