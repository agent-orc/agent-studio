# Related

- AGT-2862 - `ng serve` pinned to `host: 127.0.0.1`, and the "One loopback
  truth" probe that tries all three spellings.
- AGT-2754 - the OrchestratorApi connector profile, which reserves
  `[::1]:5031` for the connector.
- AGT-2984 - the connector's Origin/CSRF checks, which accept
  `http://localhost:4011` and `http://[::1]:4011` but not
  `http://127.0.0.1:4011`.
- Code: [`serve-prebuilt-frontend.mjs`](../../../../frontend/scripts/serve-prebuilt-frontend.mjs),
  [`api.sh`](../../../../api.sh),
  [`ConnectorHost.cs`](../../../../backend/Features/Connector/ConnectorHost.cs),
  [`ConnectorOptions.cs`](../../../../backend/Features/Connector/ConnectorOptions.cs).
- Docs: [stable-release-contract.md](../../stable-release-contract.md) "One
  loopback truth", [contributor-setup.md](../../setup/contributor-setup.md).
