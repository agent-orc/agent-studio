# AGT-3184 Studio website status

- Result SHA: pending platform-owned delivery commit. Worktree source HEAD: `fc7bc1c24fee4e63edd626f479ec6701df0513e4`; this task does not create commits.
- Implemented application lifecycle D15 option A: a dedicated VM with one Compose stack for the networked monolith, static frontend, and HTTPS Caddy edge. Public ACME and private internal TLS configurations are included.
- Remote owner creation requires the installer code printed by `setup.sh`; loopback bootstrap remains available. Only the named frontend container can supply forwarded HTTPS to the API. The API health check uses the configured hostname.
- The browser sends authentication POSTs while the live-update hub is offline. An HTTPS Studio without an explicit Update Service endpoint does not poll port 5039.
- Verification: solution and Angular builds passed; 28 focused backend tests, 82 backend guards, 14 focused frontend tests, and two focused Playwright specs passed. Runner and Task Server guard filters had no matching tests. `npm --prefix frontend run lint`, Caddy validation, Compose config, YAML parse, shell syntax, and the live Compose HTTPS login and board smoke passed. Playwright screenshots are in the task results directory.
