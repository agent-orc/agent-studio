# Ideas

Hypotheses, open questions, ruled-out approaches.

- **Ruled out: bind the backend on `[::1]` too.** `[::1]:5031` belongs to the
  connector, so the backend stays on `127.0.0.1`.
- **Ruled out: tell browsers to open `http://127.0.0.1:4011`.** The connector
  rejects that origin in its default configuration.
- **Open: dual bind for `ng serve`.** `frontend/angular.json` stays on
  `host: 127.0.0.1` per AGT-2862, so `http://localhost:4010` still pays the
  fallback against the dev server.
- **Open: operator scripts outside this repository.** The devspace launcher and
  `orch-*.sh` / `watchdog-stable.sh` still spell `localhost:5031`.
- **Not measured: browser fallback timers.** Browsers use their own Happy
  Eyeballs timer.
