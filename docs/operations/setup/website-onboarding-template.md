# Website onboarding copy template

> Publishing note: this is source copy for the marketing website. MKT/AOW owns
> website integration, layout, analytics, and final publication. Product
> engineering maintains the commands and product claims below.

## Install Agent Studio

Download `agent-studio-setup` for Linux x64 or `agent-studio-setup.exe` for
Windows x64 from the [latest release](https://github.com/agent-orc/agent-studio/releases/latest).
Download `SHA256SUMS` from the same release and verify the executable before
running it. The executable and product binaries are self-contained; no .NET
installation is required.

The default path installs the complete one-box stack using Docker Engine on
Linux or Docker Desktop with the WSL2 backend on Windows. It pins the release,
starts the Task Server, Engine, Studio UI, and one runner container, waits for
health, and prints the browser URL. The UI listens on loopback by default.

```sh
./agent-studio-setup
```

For remote Linux deployments, use `--mode control-plane` on the Task Server
machine and `--mode agent-host` on each runner machine. See the
[install guide](./install.md) for platform requirements, offline bundles,
unattended answers, update, rollback, and removal. Native full Studio on
Windows remains a separate delivery item; the existing Windows fallback
profile is for recovery and does not contain the complete local product.
