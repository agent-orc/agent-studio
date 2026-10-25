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

The default path uses Docker Engine on Linux and native Windows services with
no Docker Desktop dependency. It pins the release and starts Task Server,
Engine, the Studio backend and browser UI, and a local Runner. Windows setup
requests administrator rights through UAC, checks Git for Windows and the
local ports, and asks about provider login and GitHub access. The UI listens
on loopback by default.

```sh
./agent-studio-setup
```

For remote Linux deployments, use `--mode control-plane` on the Task Server
machine and `--mode agent-host` on each runner machine. See the
[install guide](./install.md) for platform requirements, offline bundles,
unattended answers, update, rollback, and removal. Hosts without Docker use
`--target native`; this is the Windows default. The Linux native path uses
systemd. An unsigned Windows build may show SmartScreen; verify the release
checksum before continuing.
