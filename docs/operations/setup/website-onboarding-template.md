# Website onboarding copy template

> Publishing note: this is source copy for the marketing website. MKT/AOW owns
> website integration, layout, analytics, and final publication. Product
> engineering maintains the commands and product claims below.

## Download Agent Studio

Download the installer for your machine from the
[latest release](https://github.com/agent-orc/agent-studio/releases/latest):
`agent-studio-setup.exe` for Windows x64 or `agent-studio-setup` for Linux x64.
Download `SHA256SUMS` alongside it and verify the executable before running.
The installer and installed product binaries do not require a .NET runtime.

## Install on one machine

Start Docker Desktop with WSL2 on Windows or Docker Engine with Compose on
Linux, then run the installer. Accept the Docker runtime and port `4011`.
It starts the full Studio stack and a runner container, checks health, and opens
the UI at `http://localhost:4011`. Credentials for coding tasks can be added
later. See the [install guide](./install.md) for prerequisites, screenshots,
unattended setup, updates, rollback, and uninstall.

## Remote control plane

For a separate Task Server and runner host, use the
[Docker control plane](./control-plane-docker.md) and
[multi-machine guide](./multi-machine.md). A native Windows fallback profile
for Task Server, Engine, and connector is documented in the
[Windows runbook](./windows-fallback-runbook.md).
