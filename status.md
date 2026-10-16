# AGT-3184 Studio website status

- Result SHA: pending browser-smoke fix.
- Implemented application lifecycle D15 option A: a dedicated VM with one Compose stack for the networked monolith, static frontend, and HTTPS Caddy edge. Public ACME and private internal TLS configurations are included.
- Remote owner creation requires the installer code printed by `setup.sh`; loopback bootstrap remains available. Only the named frontend container can supply forwarded HTTPS to the API. The API health check uses the configured hostname.
- The browser sends authentication POSTs while the live-update hub is offline. An HTTPS Studio without an explicit Update Service endpoint does not poll port 5039.
- Verification: pending focused checks for this fix round.
