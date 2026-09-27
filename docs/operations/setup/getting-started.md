# Getting started

The verified installation path for this checkout is the source-built one-box
Docker Compose stack. Start with [Docker operations](./docker.md) for the exact
command, credential bootstrap, backup, update, and troubleshooting steps.

The published-image path uses the same root `docker-compose.yml` and is checked
by post-release CI. It is intended for pinned release versions after that
check completes. The current one-box deployment uses the distributed Task
Server and Engine, with the BFF serving `/api/v1`; other dev-seat
routes still have the option C coverage limit recorded in the
[connector gap](./docker-compose-connector-gap.md).

Source contributors can use the [contributor setup](./contributor-setup.md).
