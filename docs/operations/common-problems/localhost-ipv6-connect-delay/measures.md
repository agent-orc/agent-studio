# Measures

Fix attempts and their status. Status vocabulary: `tried`, `applied`, `works`, `regressed`.

| Status | Date (UTC) | Measure | Owner | Outcome |
|---|---|---|---|---|
| applied | 2026-10-12 | Prebuilt frontend server binds `127.0.0.1` and `[::1]` on the same port with shared handlers | AGT-3092 | Unit-tested in `serve-prebuilt-frontend.test.mjs`; Stable confirmation pending the operator copy of the server into the devspace root and a restart |
| applied | 2026-10-12 | In-repo API clients spell `127.0.0.1` (`e2e/helpers/api.ts`, `proxy.conf.json`, `tag-manager-screenshots.mjs`, `generate-project-map.mjs`) | AGT-3092 | One spelling for operators and agents to copy into curl and PowerShell |
| applied | 2026-10-12 | `api.sh` comment names the connector as the owner of `[::1]:5031` | AGT-3092 | Guards against "fixing" the delay by binding the backend on `[::1]` |
