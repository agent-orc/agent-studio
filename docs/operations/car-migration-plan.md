# Koordinationsplan: CAR-Migrations-Kette AGT-2370 → 2371 → 2372 → 2373

**Status:** completed 2026-09-24 (`AGT-2373`) | **Phase:** T1-T4 complete | **Mode:** coordinated operator batch
**Sources verified in code:** `runner/`, `backend/Features/Cli/Execution/`, `backend/Features/Runner/`, CodingAgentRunner 0.7.0, `contracts/TaskServer.Contracts/`, `deploy/`, `docs/operations/{umsetzungsplan-zielbild,zielbild-komponenten-protokolle,execution-model-shift}`, `docs/concepts/distributed-agent-studio-target-architecture.md`

> The original planning baseline below is retained to explain the migration sequence. CAR is now the only card-run CLI execution layer. Legacy engine settings, raw Runner spawn, and Studio-owned card-run argv construction described below are historical.

---

## 0. Current decision summary

1. **The pre-migration baseline had four CLI launch paths, not three.** The chain includes the project-chat path so no unstructured Codex invocation is left behind.
2. **CodingAgentRunner 0.7.0 delivered the prerequisite prompt, clean-context, and adapter work used by T1 and T2.** T2 found four narrower public composition gaps: `public-clean-context-lease`, `public-hardened-spawner-composition`, `public-cli-launch-overlay`, and `public-pre-spawn-health`. They stay in PROJ-011 and are bridged without copying CAR internals.
3. **The execution specification on the claim was a required T0 dependency and is now implemented.** It carries `cliType`, `model`, `thinkingLevel`, `permissionMode`, and `contextMode` so the driver change preserves the card's execution choices.
4. **Remote hot migration uses detached-worker durability.** `KillMode=process` and worker-level reattach allow runner deploys during a wave. This does not imply local backend reattach.
5. **Container launch remains a tranche after T2.** T1 and T2 make the CAR spawner seam reachable so container launch can be implemented once.
6. **T1 through T4 run as coordinated operator batches.** They are not independently auto-claimed cards.

---

## 1. Pre-migration baseline: four CLI launch paths

| # | Pfad | Einstieg | Mechanik |
|---|---|---|---|
| 1 | Backend lokal, Kartenrun | `backend/Features/Runner/ProjectRunner.cs:2704-2706` → `CliExecutionServiceBase.cs:348-626` | `new Process` + `p.Start()` (`:667`); `WindowsHandleScrubSpawner` nur bei `ClaudeCli:UseHandleScrub=true` (`BuiltInCliBehaviors.cs:437-453`, produktiv **aus**) |
| 2 | Backend lokal, Conflict-Resolution | `ProjectRunner.cs:1745-1799` | gleicher Stack, eigener jobKey `…:conflict-resolution` |
| 3 | Runner remote, Coding | `RemoteTaskRunner.cs:528` → `DurableAgentProcess.Start` → `ProcessRunner.RunAsync` (`DurableAgentProcess.cs:277`) | detached Worker, `RUNNER_CLI_BIN`/`RUNNER_CLI_ARGS`, Prompt auf stdin, Rohzeilen |
| 4 | Runner remote, Project-Chat | `RemoteProjectChatRunner.cs:89` | roh `codex exec --experimental-json --sandbox read-only`, Prompt auf stdin — **im Kartentext nicht erfasst** |
| — | CAR `CliRunEngine` | CAR-Repo `src/CodingAgentRunner/Execution/CliRunEngine.cs` | im Produkt **nirgends** instanziiert |

**Nicht anfassen:** `ProcessRunner` ist außerdem der generische Prozessstarter für Git und Verifikationskommandos (`RemoteReviewWorkspace.cs:229,290,360,515,529,559,581`, `RemoteProjectChatRunner.cs:274,303`). 2373 darf ihn nicht ersatzlos löschen, nur seinen CLI-Anteil.

**Was der Remote-Pfad heute tatsächlich tut** (`docs/operations/setup/linux-runner-host.md:254,266`): `claude -p`, Prompt auf stdin, **Klartext-Ausgabe** (kein `stream-json`), **kein Permission-Flag** (die globale Host-Config entscheidet), **geteiltes Config-Home** (kein Clean-Context), **Modell = Host-Konfiguration**. Der CAR-Pfad ändert alle fünf Punkte gleichzeitig — das ist der eigentliche Migrationsrisikokern, nicht der Prozessstart.

---

## 2. API-Gap-Analyse CAR 0.6.x

> Historical planning input. CodingAgentRunner 0.7.0 closed the prerequisite package described here. The implemented T2 boundary and the remaining 0.7.0 public API gaps are authoritative in the T2 section below.

### 2.1 Was CAR mitbringt und nicht nachgebaut wird

| Fähigkeit | CAR-Fundstelle |
|---|---|
| argv-Aufbau je CLI inkl. Permission-/Reasoning-Flags | `Execution/BuiltInDescriptors.cs:52-127`, `Model/CliPermissionMode.cs:94-112` (identisch zu `BuiltInCliBehaviors.cs:142-192/824-874`) |
| stream-json → typisierte Events | `Adapters/{Claude,Codex}EventAdapter.cs` — das Backend nutzt sie **bereits** (`BuiltInCliBehaviors.cs:269,1011`) |
| 3-wertiges Outcome + Stop-Reason-Semantik | `Model/RunStatusClassifier.cs`, `docs/process-termination.md` |
| Prozessbaum-Kill (taskkill /T /F bzw. `entireProcessTree`) | `CliRunEngine.cs:662-688` |
| Clean-Context-Mechanik (`CLAUDE_CONFIG_DIR`/`CODEX_HOME`) | `Execution/CleanContextSpec.cs`, `CliRunEngine.cs:279-288` |
| Umgebungs-Härtung, npm-Shim-Heilung, Git-Guard | `Execution/Hardening/*`, `Execution/NpmShimHealer.cs` |
| Quota-Cache, Wait-on-Quota, Interrupt-Events | `Quota/*`, `CliRunEngine.cs:387-489` — im Studio-Fork **nicht** vorhanden |
| Silence-Watchdog, phase-aware | `Events/RunWatchdog.cs`, `Events/WatchdogPolicy.cs` |
| Spawner-Seam | `Abstractions/CliProcessSpawner.cs`, `CliRunEngine.cs:195-209` |

### 2.2 Lücken mit Verdikt

| Bedarf (Ist-Verhalten) | CAR 0.6.x | Verdikt |
|---|---|---|
| Detached Worker + Reattach über Daemon-Neustart | In-Process-Registry `CliRunEngine.cs:38`; kein Adopt-by-PID | **Host behält** `DurableAgentProcess`; CAR läuft *im* Worker. Kein CAR-Paket (wäre Orchestrierung → `execution-model-shift` §5) |
| `output.jsonl` mit monotoner Sequence, ab Offset aus fremdem Prozess folgbar (`DurableAgentProcess.cs:202-222`) | `RunLogStore` schreibt `<runDir>/<stream>.jsonl` **ohne** Sequence; `ReadMerged` liest alles und sortiert nach Timestamp (`RunLogStore.cs:98-124`) | **Host behält** sein `output.jsonl`, gespeist aus `ICliDriver.OnOutput`. CAR-Log per `IRunLogPathProvider` in dasselbe Worker-Verzeichnis lenken. Optional **CAR-D** (Sink-Seam) beseitigt die Doppelschreibung |
| `result.json` terminal + atomarer Rename | nur In-Process `RunEnded`/`OnFinished` | **Host behält** (`DurableAgentProcess.cs:300-323`) |
| Sentinel-Grammatik / Environment-Blocker-Grammatik in der Bibliothek | `IInterruptClassifier` existiert, aber **kein Consumer-Seam**: `BuiltInDescriptors` ist `internal`, `CliRunEngine` `internal sealed`, `CliRunner` baut den Katalog fest (`CliRunner.cs:40`), `CliOptions` hat kein Classifier-Feld | **CAR-C** (nicht blockierend). Interim: `SentinelScanner`/`CheckEnvironmentBlocker` bleiben beim Host auf den Rohzeilen — byte-gleiches Verhalten |
| Facts für `ExecutionOutcomeAdapter` (StdOut/StdErr/ExitCode/TerminalEvent/FinalAssistantOutput/SessionId, `contracts/…/ExecutionOutcomeContracts.cs:95-110`) | Events ja, **kein** Final-Reply-Aggregat | **Stufe A (T1):** weiter aus Rohzeilen. **Stufe B (nach Parity):** aus Events. Optional **CAR-F** |
| `JOB_RESULTS_DIR` (`CliExecutionServiceBase.cs:449-455`, `DurableAgentProcess.cs:284`) | `CliRunRequest.ExtraEnvironment` | **kein Gap** |
| Prompt auf stdin (Remote heute) | Claude-Deskriptor legt den Prompt als **letztes argv** ab (`BuiltInDescriptors.cs:60`); Codex nutzt stdin (`:123`) | **CAR-A**, Priorität hoch. Zwei Gründe: (a) Grenzen — Windows `CreateProcess` 32 767 Zeichen, Linux `MAX_ARG_STRLEN` 128 KiB je Argument; (b) der komplette Prompt steht in `ps` / `/proc/<pid>/cmdline` auf einem geteilten Host. Entschärfung: lokal läuft Claude **heute schon** über argv (`BuiltInCliBehaviors.cs:189-192`) — die Grenze ist empirisch also nicht akut, aber remote gibt es sie heute gar nicht |
| Prozessgruppen-Isolation (setsid) | nicht vorhanden | Kein Verlust: der Parameter existiert im Runner, wird aber **nie** mit `true` aufgerufen (`ProcessRunner.cs:38,43`). Für die Container-Tranche: **CAR-G** |
| Win32-Job-Object-Container (fängt abgekoppelte Enkel: Playwright, `node serve.cjs`) | nicht vorhanden | **Host behält** `backend/Features/Cli/Execution/Win/TaskProcessReaper.cs` |
| Wall-Clock-Timeout (`RUNNER_RUN_TIMEOUT_SECONDS=3600`, `RunnerOptions.cs:126`) | nur Silence-Watchdog | **Host behält** CTS + `driver.Stop(...)`. `RunStopReason` hat kein `Timeout` → mappt auf `Watchdog`; kosmetisch **CAR-I** |
| Output-Budget für die Klassifikations-Eingabe (2 MB Tail, `ProcessRunner.cs:26`) | 5000-Zeilen-Ringpuffer (`CliRunEngine.cs:444`) | **Host behält** `BoundedOutputBuffer` |
| Clean-Context: Credential-Datei **verlinkt**, damit der OAuth-Refresh durchschreibt | CAR **kopiert** (`CleanContextPreparation.cs:174`); Studio verlinkt (`backend/…/CleanContextPreparation.cs:109,121,262-303`) | **CAR-B**, blockierend für Clean-Mode. Sonst Regression „OAuth-Token-Roulette" (AGT-2066), und auf `agent-runner-01` ist der Host-eigene Token die einzige Anmeldung (`linux-runner-host.md:194-208`) |
| Registry laufender Runs über Prozessgrenzen + Reaper (`CliExecutionServiceBase.cs:1499-1530`) | `IRunLogPathProvider.GetActiveJobsFile()` deklariert, **kein Produzent/Konsument** (repo-weit nur Tests) | **Host behält.** CAR: **CAR-H** — implementieren **oder** Deklaration streichen (Hygiene für 2373) |
| Toleranter `rate_limit_event`-Parser (camelCase **und** snake_case), `system/init`-Kontext | CAR-Adapter kennen nur die enge Form | **CAR-E** — sonst Regression bei Quota-Anzeige und T1a-Panel (`Execution/Adapters/ClaudeRateLimitEventParser.cs`, `ClaudeInitContextParser.cs`) |
| Claude-Session-Heartbeat gegen stdout-stille Phasen | `LivenessSpec` (Side-Channel-Konzept) + `CliRunInfo.CleanContextHome:35` | **Host adaptiert** (`ClaudeSessionHeartbeat.cs`) — der Home-Pfad ist die einzige benötigte Zuleitung |
| Antigravity/`agentapi` | Studio spricht `agentapi` **ohne** Permission-Flags/stream-json (`BuiltInCliBehaviors.cs:1245-1276`); CAR-Deskriptor setzt `-o stream-json`/`--skip-trust` | Divergenz: in T2 gesondert entscheiden, sonst am Alt-Pfad lassen und in 2373 **begründet** stehen lassen |
| **Ausführungs-Spec auf der Leitung** | — | **Studio+Server-Paket (T0b)**, siehe §3 |
| `SendInput` / Live-Steer | CAR: `SendInput` existiert, aber Claude-Runs redirecten stdin gar nicht und Codex' stdin wird nach dem Prompt geschlossen (`CliRunEngine.cs:296-306`) | **Kein Gap**: das Backend hat repo-weit **keinen** Produktivaufrufer von `SendInput`; Steer läuft als Marker + Resume-Run (`ProjectRunner.cs:4238-4263`, `TaskRunnerEndpoints.cs:61`) |

### 2.3 CAR-Arbeitspakete (PROJ-011, Karten `CAR-*`, englische Titel)

| ID | Titel | Größe | Blockiert |
|---|---|---|---|
| CAR-A | `Claude: option to send the prompt on stdin instead of the last argv` | S | T1 (empfohlen vorher) |
| CAR-B | `Clean context: share refreshable credential files by link, not by copy` | S | T1/T2 im Clean-Mode |
| CAR-E | `Adapter tolerance: camelCase rate_limit_event + system/init context event` | S | T2-Parity |
| CAR-C | `Consumer-supplied interrupt classifier and descriptor overlay` | M | — (ermöglicht 2373-Endzustand) |
| CAR-D | `Pluggable run-output sink instead of an owned per-stream log` | M | — |
| CAR-F | `Terminal-reply aggregate on TurnCompleted / RunEnded` | S | Stufe B |
| CAR-G | `POSIX process-group isolation for spawned CLIs` | S | Container-Tranche |
| CAR-H | `Implement or remove the active-run registry declaration` | XS | 2373-Hygiene |
| CAR-I | `RunStopReason.Timeout` | XS | — |

**Release-Schnitt:** A + B + E = **CAR 0.7.0**, vor T1. C/D/F/G/H/I = 0.8.0, parallel zur Kette.

### 2.4 Leitplanke: was nicht nach CAR wandert

Wörtlich aus `execution-model-shift` §5: *„Keine Orchestrierung wandert hinein. Placement, Mount-Berechnung, Container-Lebenszyklus, Worktrees, Karten, Attempt-Records und Lanes sind Sache des Ausführungs-Hosts und des Task-Servers. CAR darf das Board nicht kennen."* Damit bleiben beim Host: Detached-Worker + Reattach, Lease/Heartbeat, Outbox, `ImmutableResultEnvelope`, Salvage/Teardown, Orphan-Policy, Job-Objects, Sentinel-**Semantik** (Lane-Zuordnung), Sequence-Log.

---

## 3. Schnitt in Tranchen

### T0 — Vorleistung (neu, steht in keiner Karte)

**T0a · CAR 0.7.0** (CAR-A, CAR-B, CAR-E) — Release auf nuget.org.
**T0b · Ausführungs-Spec auf der Leitung.** Neues Feld im v1-Claim: `cliType`, `model`, `thinkingLevel`, `permissionMode`, `contextMode` (heute nur beim Epic-Planning-Endpoint vorhanden und dort **nur geloggt**: `RemoteTaskRunner.cs:501`). Server + Runner in **einem** Change, additiv-first (`target-architecture` §10: *„Task Server accepts both old and new clients, then Runners and Studio upgrade"*).

**Abnahme T0:** CAR 0.7.0 auf nuget.org und in beiden `csproj` gepinnt · ein Remote-Lauf beweist, dass Modell und Thinking-Level der **Karte** verwendet werden (Log-Zeile + `RunRecord`, vgl. AGT-2386) · alter Runner läuft am neuen Server weiter.

**Umgesetzt am 2026-07-28 (T0b, Server + Runner in einem Change).** Der Claim trägt jetzt ein additives Feld `runSpec` (`RunSpecDto` in `backend/Shared/Lease/RunLeaseWireModels.cs` und `runner/WireModels.cs`, an `RunnerClaimResponse` angehängt; `contracts/` unberührt, keine Protokollversion erhöht — die Kopplung aus §6.3 bleibt T4). Quellen je Feld, serverseitig in `LeaseEndpoints.BuildRunSpec` gebündelt und sowohl im frischen Claim als auch im Replay gesetzt: `cliType` = `task.cliType`, normalisiert wie `CliRouter.Get` (unbekannt/leer ⇒ claude); `model` / `thinkingLevel` = die Pins der Karte, bei einem Epic überschrieben von `EpicPlanningModel` / `EpicPlanningThinkingLevel` des Projekts — dieselbe Quelle wie `ProjectRunner` (~2240/2246) und `/api/runner/epic-planning-prompt`; `permissionMode` = `ProjectSettingsService.ResolveCliMode`, `contextMode` = `ResolveContextMode` — die identischen Live-Lookups des lokalen Spawns (`ProjectRunner` ~2647), ein Projekt-Toggle wirkt also ab dem nächsten Claim.

*Fallback semantics:* Every field is optional. A server without T0b sends no `runSpec`, and a runner without T0b ignores it, so `RUNNER_CLI_BIN` / `RUNNER_CLI_ARGS` decide as before. `AgentCliProcess.Resolve` translates model and reasoning pins only when the requested CLI is the resolved CLI (claude: `--model X` + `--effort L`; codex: `-m X` + `-c model_reasoning_effort="L"` + `-` for the stdin prompt). If the requested CLI is unavailable, the host CLI and its `RUNNER_CLI_ARGS` win, the card model and thinking pins are dropped completely, and the mismatch remains visible through `note=...`. The spec is persisted additively in `PersistedRunnerSlot.RunSpec` and the detached worker's `spec.json`; a pre-T0b file still loads with `null` and runs unchanged, which preserves the hot-deploy contract from §4. The spawn proof line is written to the journal and task log: `[runner] spec cli=... model=... thinking=... permission=... context=... source=card|runner-options|card-cli-fallback(model-pins-dropped)`.

*Live incident and fix (2026-07-28, AGT-2400 / AGT-2402):* A claude-pinned card reached a codex-only host. The original fallback retained codex but appended `-m claude-opus-5`, which Codex rejected with an OpenAI 400 and exit 1 before the run escalated as `CliCrash`. The resolver now reports `<cli-default>` for model and thinking on a foreign-CLI fallback and uses only the fallback host's configured arguments. Claim-side CLI capability matching remains tracked by the follow-up card `claim-cli-capability-matching`.

*Kanarienvogel-Befund (28.07., AGT-2355, erster echter Codex-Remote-Lauf über die Spec):* Ende-zu-Ende grün — Claim → `codex exec` mit `-m gpt-5.6-sol` → Lieferung → saubere NeedsInput-Eskalation mit Salvage/Result-Transfer. Kosmetischer Restbefund für T1: weil `RUNNER_CLI_ARGS` als Basis erhalten bleibt, stehen Modell-/Effort-Flags **doppelt** auf der Kommandozeile (Env-Basis vor dem Stdin-`-`, Spec-Args dahinter); Codex parst last-wins, der Karten-Pin gewinnt korrekt, aber die Spec-Args gehören vor das `-` und die Env-Duplikate raus, sobald T1 die Spawn-Stelle ohnehin anfasst.

*Bewusst offen:* (1) `permissionMode` und `contextMode` werden transportiert, persistiert und geloggt, aber **nicht** in Flags übersetzt — Permission-Injektion und Clean-Context remote sind zwei der fünf Verhaltenssprünge aus T1, und Clean-Context blockiert zusätzlich auf CAR-B (sonst Token-Kopie statt Link). (2) Die Modell-**Qualifizierung** läuft nicht im Claim: sie braucht gerenderten Prompt, Modellkatalog und Projekt-Historie; transportiert wird der Pin der Karte. (3) Ein Thinking-Level, das die Karte nicht setzt, bleibt `null` statt auf die Default-Stufe der Leiter zu springen — remote entsteht so kein Reasoning-Flag, das niemand angefordert hat. (4) Der v1-Claim (`/api/v1/runners/{id}/claims`, `Contract.ClaimResponse`) und der Einzel-`lease/acquire`-Pfad tragen weiter keine Spec und fallen auf die Runner-Konfiguration zurück. (5) `task.contextMode` wird vom Scanner gar nicht erst gelesen (Altbefund) — der Claim spiegelt den lokalen Aufruf inklusive dieser Lücke.

*Tests:* `backend.Tests/RemoteRunnerEndToEndTests.Daemon_claim_carries_the_cards_execution_spec_and_the_runner_builds_its_cli_args` (Claim trägt die Spec der Karte; eine Codex-Karte mit unerfüllbarer Stufe wird serverseitig auf eine unterstützte aufgelöst) und `runner.Tests/RunSpecInvocationTests` (Args-Bau mit/ohne Spec, CLI-Wechsel, Resume-Args, Roundtrip durch `spec.json` und den Slot inklusive Alt-Format).

*Prompt-enrichment extension (2026-08-01):* The monolith composes the optional,
audited enrichment block into the existing `RunSpec.ModeFraming` value before
lease acquisition. The runner persists that one composed `RunSpec` in
`PersistedRunnerSlot`, so daemon replacement cannot relaunch an attempt without
its mode contract or audited enrichment. There is no parallel enrichment wire
field or runner-side sidecar lookup. Older servers remain compatible because
an absent `RunSpec` or `ModeFraming` retains the previous prompt behavior. The
separated v1 `Contract.ClaimResponse` is unchanged because that server does not
publish monolith job-folder enrichment artifacts.

### T1 = AGT-2370 · Runner-Daemon auf CAR

**Schnitt.** Der detached Worker (`DurableAgentProcess.RunWorkerAsync`, `DurableAgentProcess.cs:241-308`) treibt das CLI künftig über `ICliDriver` statt `ProcessRunner.RunAsync`. Alles außerhalb des Workers bleibt unberührt.

**Schritte**
1. **Bezugsweg:** `PackageReference CodingAgentRunner [0.7.0]` in `runner/AgentRunner.csproj` — **nicht** ProjectReference. Begründung: der Runner ist ein separat deploytes Artefakt (`/opt/agent-host`), das Backend pinnt bereits als Paket (`OrchestratorApi.csproj:43`); zwei Bezugswege wären genau die Versions-Drift, die §6 des Protokoll-Docs anprangert.
2. **Worker:** `CliRunner` mit `CliOptions{ ClaudePath/CodexPath, EnvironmentOverrides }`; `StartAsync(new CliRunRequest{ RunId=attemptId, Prompt, WorkingDirectory=repoPath, Model, ThinkingLevel, PermissionMode, ContextMode, ResumeSessionId, ExtraEnvironment={JOB_RESULTS_DIR} })`.
3. **Log-Senke:** `OnOutput` → das bestehende `output.jsonl` mit Sequence (das ist das Reattach-Protokoll, es bleibt). `OnRunEvent` wird zusätzlich als typisierte Spur geschrieben (`events.jsonl`) — Grundlage für Stufe B und für die spätere Server-Ingest-Kanonisierung.
4. **Terminal:** `RunEnded` + `OnFinished` → `result.json` (Exit-Code, StdOut-Tail aus `BoundedOutputBuffer`, StdErr-Tail, TimedOut). Klassifikation (`ExecutionOutcomeAdapter`, `SentinelScanner`) bleibt **unverändert**.
5. **Timeout/Kill:** CTS wie heute; bei Ablauf `driver.Stop(runId, RunStopReason.Watchdog)` statt Prozess-Kill von außen. Lease-Verlust-Kill (`LeaseLossProcessKillTests`) über `DurableAgentProcess.Kill()` unverändert.
6. **Config-Erbe:** `RUNNER_CLI_BIN` → `RUNNER_CLI_TYPE` (`claude|codex`) + optionaler Pfad; `RUNNER_CLI_ARGS` **entfällt** (Deskriptor); `RUNNER_CLI_RESUME_ARGS` → `CliRunRequest.ResumeSessionId`. Migrationsnotiz + Fehlermeldung, wenn die alten Variablen noch gesetzt sind.
7. **Bewusste Verhaltenssprünge dokumentieren** (jeder braucht ein Parity-Szenario): Klartext → `stream-json`; kein Permission-Flag → Bypass-Modus (yolo-Default); shared → clean Config-Home (**bis CAR-B: explizit `ContextMode=shared` setzen**); Prompt stdin → argv (**bis CAR-A**).
8. **T1c (Scope-Erweiterung):** `RemoteProjectChatRunner.cs:89` auf CAR mit `PermissionMode=read-only`.

**Flag:** `RUNNER_EXEC_ENGINE=car|legacy`, Default in T1 `car`. Der Runner hat als Deployable ohnehin nur Env als Konfigurationsfläche; der Schalter ist ein Rollout-Instrument mit Verfallsdatum in 2373, kein Dauer-Setting.

**Abnahme (Kartentext + Ergänzungen):** echter Remote-Lauf mit attributierten Commits, verifiziertem Push, gültigem Attempt-Record · Reattach nachgewiesen (Daemon-Restart mitten im Lauf) · Kill/Timeout ohne verwaisten CLI-Prozess · `runner.Tests` grün + neue Tests für die Ausführungsschicht · **ergänzt:** ein Lauf mit dem größten real vorkommenden Prompt · Nachweis, dass der Host-eigene Claude-Token nach dem Lauf noch gültig ist · Notiz „welche CAR-Fähigkeit fehlte" mit den erzeugten `CAR-*`-Karten.

### T2 = AGT-2371: local backend on CAR

**Implementation status, 2026-08-02.** `GenericCliExecutionService` now selects an execution engine for every normal local card run and conflict-resolution run. Claude and Codex use `ICliDriver` from CodingAgentRunner 0.7.0 when the effective engine is `car`. Antigravity continues through the explicit legacy adapter because Studio's current integration speaks the `agentapi` conversation protocol, not CAR's Antigravity stream and permission protocol. This is a deliberate compatibility boundary, not an unsupported-CLI fallback.

The local rollout selector has one process-wide emergency override, two
persisted tiers and one fixed fallback:

1. Process environment `RUNNER_EXEC_ENGINE=car|legacy`.
2. Project override.
3. Workspace default.
4. Platform default, `car`.

The environment selector has highest precedence so an operator rollback cannot
be masked by a persisted project or workspace value. Both persisted tiers also
accept `car` or `legacy`. Blank environment input falls through to the persisted
hierarchy; unknown environment or persisted values fail loudly. The standalone
runner uses the same environment variable and also defaults to `car`.

**Implemented ownership split.** The boundary is intentionally not a wholesale replacement of Studio behavior:

| Capability | Owner after T2 | Implemented treatment |
|---|---|---|
| CLI descriptors, argv/stdin transport, permission flags, common thinking normalization, process lifecycle, typed events | CAR 0.7.0 | Claude and Codex start through `ICliDriver`; Studio selects CAR-A stdin for Claude and does not rebuild these capabilities. |
| Model qualification against the live Studio catalog | Studio | Qualification runs before the CAR request; CAR then applies its shared normalization. |
| Live UI rendering, raw output mirror, session capture, quota and usage parsing, token and cost ledger, terminal sentinel classification | Studio | Existing callbacks and renderers consume the CAR output and event streams. |
| Active-job persistence, orphan cleanup, Windows task job object, heartbeat and silent-completion detectors | Studio | These stay above the in-process CAR driver. |
| Clean context across attempts of one task | Studio over CAR | Studio acquires or reuses its task-stable linked home, passes CAR `ContextMode=shared`, and injects `CLAUDE_CONFIG_DIR` or `CODEX_HOME` through `ExtraEnvironment`. This prevents CAR from creating a second process-scoped home and preserves resume state. |
| Claude system-prompt file | Studio over CAR | A narrow public `ICliProcessSpawner` decorator adds `--append-system-prompt-file` until CAR exposes a launch overlay. |
| Antigravity `agentapi` conversations | Studio legacy adapter | `new-conversation` and `send-message` retain their current output, session and permission semantics. |

**Usage event ordering bridge.** CAR 0.7.0 publishes typed events before the matching raw-output callback. Studio's synchronous `TurnCompleted` ledger previously depended on usage and session metadata already having been parsed from that raw line. `CarCallbackBridge` therefore buffers the typed events, processes the raw line first, and then publishes the matching event batch. Live rendering still receives each line once, while ledger ordering remains unchanged.

**Removed launch copy and temporary rollback exception.** The backend-specific `Win/WindowsHandleScrubSpawner` has been removed. CAR owns Claude npm-shim repair on the CAR path. CAR 0.7.0 does not expose that healer publicly, so the existing Studio `NpmShimHealer` remains temporarily for the explicit `legacy` rollback and the non-agent `ClaudeOneShot` path only. T4 removes it with those old invocation paths. Studio uses CAR's public `ICliProcessSpawner` seam for its host overlay and process bookkeeping.

**CodingAgentRunner 0.7.0 gaps found during implementation.** These are library seams, not reasons to recreate CAR internals in Studio:

| PROJ-011 card | 0.7.0 boundary | T2 bridge |
|---|---|---|
| `public-clean-context-lease` | CAR clean homes are process-scoped; Studio needs one stable home across task attempts, host restarts, and resumes. | Local and remote consumers use the shared `TaskCleanContextStore`, then pass its lease through `ContextMode=shared` plus explicit environment overrides. Neither consumer composes a separate path. |
| `public-hardened-spawner-composition` | CAR's hardened default spawner and Windows handle scrubber are internal and cannot be publicly decorated as one composition. | Use the public `ICliProcessSpawner` contract and keep the host decorator narrow. Do not restore the deleted Studio scrubber. |
| `public-cli-launch-overlay` | `CliRunRequest` has no public extra-argv or descriptor overlay for the Claude rules file. | Add only the rules-file argument in the spawner decorator after CAR has built the launch. |
| `public-pre-spawn-health` | CAR's npm-shim repair is internal to its driver and cannot be called by a legacy or one-shot host path. | Retain the pre-existing Studio healer only for the explicit rollback and `ClaudeOneShot`; delete it in T4 when those paths leave. |

CAR's built-in descriptors and Windows helpers are also internal. T2 does not copy them. Studio-specific tolerant Claude rate-limit parsing and environment-blocker and terminal-sentinel semantics remain in the host because CAR 0.7.0 does not expose the required consumer policy seams.

**Restart behavior remains unchanged.** The local backend does not reattach to a running CLI after restart. Its startup method is an orphan reaper: it validates persisted PID, process name and start time, terminates the leftover process tree, and lets existing liveness recovery demote or reissue the interrupted run. CAR is in-process, so it cannot recover the lost stdout and stdin pipes. Remote runner reattach is a separate detached-worker capability and must not be inferred for local runs.

**Acceptance gate.** A local card run must cover agent execution, post-steps and gate through the CAR path, including SignalR live output, stop, steer through resume, and the token and cost ledger. Quota wait, model qualification, thinking level, permission mode and conflict-resolution behavior must remain at parity. T3 retains the fixture and live-run evidence gate; T4 retains final legacy removal.

### T3 = AGT-2372 · Parity-Suite

**Bauform.** Zwei Ebenen, sonst wird die Suite unbezahlbar:
*Ebene 1 — deterministisch (in der Testsuite):* aufgezeichnete `stream-json`-Fixtures und ein Fake-CLI-Binary; kein echter Modellaufruf. Vergleich Alt- vs. Neu-Pfad über dieselbe Fixture.
*Ebene 2 — Betriebsnachweis:* echte Karten, dokumentiert im Task-Ordner.

**Szenarien (je Pfad lokal/remote, sofern anwendbar)**

| # | Szenario | Prüft |
|---|---|---|
| P1 | Happy Path mit `[[TASK_DONE]]` | Event-Sequenz, `RunOutcome`, Lane `4-auto-review` |
| P2 | `[[TASK_NOOP]]` | NoOp-Sonderweg (`RemoteTaskRunner.cs:727`) |
| P3 | `[[TASK_BLOCKED:…]]` ohne Grund | Default-Reason-Text |
| P4 | `[[TASK_NEEDS_INPUT:…]]` | Lane `5-human-review` |
| P5 | Substanzielle Antwort **ohne** Sentinel | `Unknown` → human-review; **Achtung**: der Alt-Pfad scannt Klartext, der Neu-Pfad die `result`-Frame-Extraktion (`SentinelScanner.cs:78-141`) |
| P6 | Wall-Clock-Timeout | Exit 124, `TimedOut=true`, `ClassifyTimedOutResult` |
| P7 | Benutzer-Stop / Lease-Verlust | `stopped`, nicht `failed/-1` (`RunStatusClassifier`) |
| P8 | Silence-Watchdog | `RunStopReason.Watchdog`, `PhaseAwareWatchdog`-Budgets unverändert |
| P9 | Selbst-Crash (Exit ≠ 0) | `failed` + Reason aus letztem `TurnFailed` |
| P10 | Provider-Auth-Fehler | `RunnerCapabilityProbe.IsProviderAuthenticationFailure` + Capability-Report |
| P11 | Quota-Limit + Wait-on-Quota | `QuotaWaitStarted/Ended`, Neustart derselben Anfrage, **kein** doppeltes Terminal-Event |
| P12 | Bounded Same-Session-Resume | `RemoteTaskRunner.cs:611-651`, genau 1 Versuch |
| P13 | **Crash/Reattach:** `systemctl restart agent-host` mitten im Lauf | Worker überlebt (`KillMode=process`), Sequence-Fortsetzung, ein Ergebnis |
| P14 | Backend-Neustart lokal | dokumentierter Ist-Zustand: Reap + Demote `2-ready`, Worktree bleibt |
| P15 | **Salvage:** dirty Worktree, divergierter Zweig | `PickupReconciliation`, `WorktreeSalvageException`, keine stille „Delivered"-Lüge |
| P16 | **Envelope-Trio:** `baseSha` / `resultSha` / `immutableResultRef` | `ResultEnvelopeDigest.Compute` (`RemoteTaskRunner.cs:234-247`), Artefakt-Manifest-Digest, Handoff-Ack |
| P17 | **Epic-Planning read-only** | `TeardownReadOnlyAsync`, `sourceMutated=false`, kein Coding-Branch |
| P18 | **Config-Home-Isolation** | kein Zugriff auf echtes `~/.claude`/`~/.codex`; Token-Refresh im Temp-Home schreibt in die Quelldatei durch (Hardlink-Test) |
| P19 | Token-/Kosten-Ledger | `GetLastParsedTurnUsage` vs. CAR-`TurnCompleted`/`RunMetricsRecorder`, gleiche Zahlen |
| P20 | Großer Prompt (≥ 200 KB) | argv-/stdin-Grenze, Regressionsschutz für CAR-A |
| P21 | Kill-Pfad | kein verwaister CLI-Prozess, kein verwaister Worktree, Job-Object greift (lokal) |
| P22 | Rate-Limit-Frame in camelCase **und** snake_case | CAR-E-Regression |

**Guard-Test (Kartenpunkt 2).** Vorlage: `backend.Tests/Architecture/WikiPathCentralizationGuardTests.cs`. Regel: in `runner/**` und `backend/Features/{Cli,Runner}/**` darf kein `ProcessStartInfo`/`Process.Start` mit einem CLI-Binärnamen (`claude`, `codex`, `agentapi`, `gemini`) oder mit `RUNNER_CLI_*` vorkommen — außer in einer Allowlist mit Begründungskommentar (Git- und Verifikationskommandos). Zwei Instanzen: `backend.Tests/Architecture/` und `runner.Tests/`.

**Abnahme:** Suite grün · Guard scharf (bewusst gebrochene Variante schlägt fehl) · Betriebsnachweis ≥ 3 Karten lokal und ≥ 3 remote mit Grade und Gate-Status · explizite Freigabenotiz „Karte 4 darf starten" **oder** Restliste.

### T4 = AGT-2373 · Aufräumen, ADR, Doku

**Delete:** `runner/AgentCliProcess.cs` completely; the CLI portion of `runner/ProcessRunner.cs` while retaining its Git and verification responsibilities; the dead `isolateProcessGroup` parameter; the remaining duplicated `ChildHandle.cs`, `Logging/CliOutputLogStore.cs`, and `Logging/RunLogStore.cs`; both T1/T2 rollback switches; and `RUNNER_CLI_BIN`/`RUNNER_CLI_ARGS`/`RUNNER_CLI_RESUME_ARGS` including their runner-host documentation. Also delete the temporary backend `NpmShimHealer` exception when the legacy and `ClaudeOneShot` raw invocation paths are removed. The backend `Win/WindowsHandleScrubSpawner` was already removed in T2 and must not be reintroduced during cleanup.

**Konsolidieren:** genau ein CAR-Bezugsweg (PackageReference, exakter Pin `[x.y.z]` in `backend/OrchestratorApi.csproj` **und** `runner/AgentRunner.csproj`) + ein Test, der beide Pins vergleicht. Nebenbefund korrigieren: `scripts/remote-runner-onboard.sh:19` installiert `--package-id CodingAgentRunner` als dotnet-Tool — falscher Artefaktname (Zeile 222 gibt es selbst zu).

**ADR-0067** (nächste freie Nummer): *„Studio nutzt CodingAgentRunner als einzige CLI-Ausführungsschicht"* — Entscheidung, Alternativen (eigener Fork behalten / CAR nur als Typquelle), Konsequenzen, Verweis auf Target-Architecture §4/§10/§13.6 und auf E5.

**Doku:** `docs/concepts/distributed-agent-studio-target-architecture.md` (Ist = Soll), `docs/system/architecture/project-map.md`, `linux-runner-host.md`, Dossier `operations/execution-model-shift` §5 + E5 auf „erledigt".

### Reihenfolge und Parallelität

```
T0a CAR 0.7.0  ─┐
T0b RunSpec    ─┴─► T1 (2370) ──► T2 (2371) ──► T3 (2372) ──► T4 (2373)
                     └── T1c Project-Chat (parallel zu T1, gleicher Stack)
T3-Vorarbeit (Fixtures, Guard-Test, Fake-CLI) läuft ab T0 parallel — alt-pfad-agnostisch
CAR-C/D/F/G/H/I laufen durchgehend parallel im CAR-Repo (eigenes Repo, eigene Karten)
```

Echt parallelisierbar: T0a ∥ T0b · T1 ∥ T1c · T3-Vorarbeit ∥ alles. **Nicht** parallelisierbar: T1 → T2 und T3 → T4 (Freigabenotiz).

---

## 4. Hot-Production-Strategie

**Warum es überhaupt geht.** `deploy/systemd/agent-host.service` fährt `KillMode=process`: SIGTERM trifft nur die Daemon-PID, detached Worker laufen weiter, der Ersatz-Daemon verifiziert und reattacht sie vor der ersten neuen Claim (`RemoteRunnerDaemon.cs:92-121`, `RecoverLaunchingIdentityAsync:417-443`, Runbook `linux-runner-host.md:575-610`). Ein Runner-Deploy mitten in einer Welle ist damit ein etablierter, dokumentierter Vorgang. Zusätzlich liegt jede Runner-Fassung in einem unveränderlichen Release-Verzeichnis; nur der `current`-Symlink wird atomar gewechselt.

**Kanarienvogel statt Big Bang (remote).** Kein neuer Sonderpfad: der CAR-fähige Runner meldet eine zusätzliche Capability (`RunnerCapabilityProbe.Advertise`, `runner/RunnerCapabilityProbe.cs:8-60`), und die Kanarien-Karten fordern sie an (`RequiredCapabilities` im Claim-Match, `contracts/…/RunnerContracts.cs:37-38`). Kohorten: **1 Karte → 1 Welle (5 Karten) → Default**. Zwischen den Stufen: Grade, Gate-Status, Token-Ledger und `runActivity` vergleichen.

**Schattenbetrieb — bewusst nur eine schwache Form.** Zwei CLIs parallel für denselben Prompt zu starten kostet doppelte Tokens und erzeugt zwei Worktrees; das lohnt nicht. Stattdessen: die typisierte Event-Spur (`events.jsonl` aus T1, Schritt 3) wird **schon im Alt-Pfad** mitgeschrieben, indem die CAR-Adapter auf die Rohzeilen angewandt werden (das Backend tut genau das bereits). So ist die Event-Parität bewiesen, bevor der Prozessstart wechselt.

**Deploy-Reihenfolge (strikt):**
1. CAR 0.7.0 nach nuget.org (`scripts/release.sh`, Tag `v0.7.0`, Trusted Publishing).
2. Backend zuerst mit **additivem** RunSpec-Feld (alte Runner bleiben lauffähig).
3. Build-Manifest regenerieren (`scripts/release/generate-build-manifest.mjs`); `BuildIdentity.Validate` erzwingt die CAR-Artefaktzeile — ein vergessener Bump fällt hier auf, nicht im Betrieb.
4. Runner in ein neues `/opt/agent-host/releases/<release-id>` publizieren, bisherigen `current`-Zielpfad notieren, `current` atomar wechseln, `systemctl restart agent-host`, Journal prüfen. Niemals Dateien des laufenden Release-Verzeichnisses überschreiben.
5. Kanarien-Karte queuen.

**Umschaltmoment sauber halten:** vor dem Restart die Host-Kapazität kurz auf 0 setzen (RunnerCapacity, AGT-2376) — laufende Runs sind durch `KillMode=process` geschützt, neue Claims fallen nicht in den Wechsel.

**Rollback, vier Stufen:** (1) Flag zurück + Restart (~30 s, laufende Runs überleben). (2) Karte neu queuen. (3) CAR-Pin zurück auf `[0.6.0]` + Backend-Redeploy. (4) `current` atomar auf das notierte vorherige Release-Verzeichnis zurücksetzen und den Daemon neu starten. Das RunSpec-Feld ist additiv und braucht **kein** Rollback.

**Beobachtbarkeit:** die Zeile `[runner] spawning {CliBin} {CliArgs}` (`RemoteTaskRunner.cs:515`) wird ersetzt durch `engine=car cli=… model=… thinking=… permission=… context=…` — der Beweis im Log, welcher Pfad gelaufen ist, und der Filter für die Betriebsnachweis-Auswertung in T3.

---

## 5. Container-Anschluss

**Empfehlung: eigene Tranche NACH T2 — mit drei Vorleistungen INNERHALB von T1/T2.**

*Warum nicht verzahnt:* Container-Spawn im selben Change vervierfacht die Parity-Fläche (Alt/Neu × Host/Container × lokal/remote). Zusätzlich hängen an der Container-Tranche offene Entscheidungen: Mount-Konvention (E1), Credential-Weg (E2), Gate-im-Container (E6) und der CLI-Token-Refresh ohne Tunnel (Umsetzungsplan §5). Eine Migration, die auf offene Entscheidungen wartet, blockiert einen Produktivpfad.

*Warum trotzdem jetzt Vorleistungen:* der heutige Befund lautet wörtlich *„`ICliProcessSpawner` ist ein Hook in einem Codepfad, den das Produkt gar nicht durchläuft"*. Wenn T1/T2 den Hook nicht erreichbar machen, wiederholt sich das.

| Vorleistung | Wohin | Inhalt |
|---|---|---|
| V1 | T1 + T2 | Genau **eine** Konstruktionsstelle für `CliOptions`/`CliRunner` je Deployable (`ExecutionEngineFactory`) — der spätere Injektionspunkt für `CliOptions.Spawner` |
| V2 | T1 + T2 | Alle Pfade explizit als Parameter (`WorkingDirectory`, `ExtraEnvironment`, Clean-Home-Pfad) statt implizit aus dem Host — Vorbereitung auf die harte Konvention Host-Pfad = Container-Pfad |
| V3 | T2 | Binärauflösung, Versions-Probe und Quota-/Environment-Inspektion hinter eine Host-Abstraktion (`BinaryResolver.ResolveExecutable`, `CliRunEngine.TestCliPath:153-183`, `EnvironmentInspector`, `ClaudeOAuthUsageProbe`) |

*Was bereits existiert:* `runner/Dockerfile` baut das agent-host-Image **mit vorinstallierten CLIs** und dokumentiert die Container-Grenze als Isolationsgrenze. Der Container-Spawn ist die Verallgemeinerung dieses Musters auf den lokalen Pfad.

*Schnitt der Folgekarte:* „Container-Spawn in der CAR-Ausführungsschicht" — `ICliProcessSpawner`, der die fertige `ProcessStartInfo` in einen `docker run`-Aufruf umschreibt, plus V3-Varianten, plus die E4-Umschaltbedingung (*N* aufeinanderfolgende Container-Läufe mit gültigem Attempt-Record und verifiziertem Push; *N* und die Startzeit-Schwelle gehören **in die Karte**).

---

## 6. Release-Verzahnung

**CAR ist ein öffentliches nuget-Paket** (pre-1.0, Apache-2.0, `agent-orc/runner`). Release = Tag `v*.*.*` → GitHub-Workflow → nuget.org via Trusted Publishing; die Paketversion kommt aus dem Tag.

**Bump-Politik für diese Kette:**
- **0.7.0 (Minor).** CAR-A ist eine *Option* (`CliOptions.ClaudePromptTransport`, Default bleibt `Argv`) — kein Fremd-Consumer bricht; das Studio setzt `Stdin`. CAR-E ist rein additive Toleranz.
- **CAR-B ist ein Bugfix, kein Feature.** Eine Credential-Datei zu kopieren, die die CLI im Betrieb erneuert, ist für **jeden** Consumer falsch. Default-Verhalten ändern (Link mit Copy-Fallback), Eintrag unter `### Fixed` mit ausdrücklichem Hinweis.
- **CAR-C** öffnet den Katalog (`ICliCatalog`-Injektion) — additive Überladung, 0.8.0.
- Verhaltensänderungen ohne Signaturbruch gehören genauso in `### Changed` wie API-Brüche (README-Zusage: *„pin a version and watch releases"*).

**Gepaarte Releases (AGT-2132):** CAR ist die vierte Artefaktlinie und dem Manifest bereits bekannt (`BuildIdentity.cs:22`). Kopplung, in T4 abgesichert:
1. Beide `csproj` pinnen **dieselbe** exakte CAR-Version — mit Test.
2. Build-Manifest führt Version + Commit + Integrity der CAR-Artefaktzeile; abweichende Runner-/Backend-CAR-Version fällt in der Preflight-Validierung durch.
3. Die RunSpec-Erweiterung erhöht die Protokollversion; der Handshake weist inkompatible Runner **sichtbar** ab.
4. Betriebsregel bis dahin: nach jedem Backend-Deploy den agent-host nachziehen und `repositoryUrl` prüfen.

---

## 7. Aufwand, Ausführungsform, Sofortstart

| Tranche | Aufwand | Ausführungsform | Begründung |
|---|---|---|---|
| T0a CAR 0.7.0 | **M** | Karten in PROJ-011 (`CAR-*`), card-scoped | eigenes Repo, isolierte Library-Änderungen, deterministische Tests |
| T0b RunSpec | **M** | **koordinierter Batch** | Server + Runner in einem Change; Protokolländerungen nie getrennt mergen |
| T1 AGT-2370 | **L** | **koordinierter Agent-Batch** (Marathon-Form) | Produktivpfad, fünf gleichzeitige Verhaltenssprünge, Deploy-Kopplung |
| T2 AGT-2371 | **L** | **koordinierter Agent-Batch** | 96 KB + 73 KB Kernpfad, sieben Dateilöschungen, Live-UI-Kopplung |
| T3 AGT-2372 | **M** | gemischt: Fixtures/Guard-Test als Karten, Betriebsnachweis orchestriert | Testarbeit ist isoliert card-scoped tauglich |
| T4 AGT-2373 | **M** | **koordinierter Batch** | Löschung + ADR + Doku in einem Commit-Satz, sonst tote Verweise |

### Die ersten drei Arbeitspakete

**AP1 · CAR-Repo · S · sofort startbar** — `Claude: option to send the prompt on stdin instead of the last argv`. `CliOptions.ClaudePromptTransport { Argv | Stdin }` (Default `Argv`), `BuiltInDescriptors.ClaudeLaunch` liefert bei `Stdin` einen `LaunchSpec.StdinPayload`. Zuerst messen: größte reale `prompt.md` + `RemoteRunPrompt.Build`-Anhang gegen Windows 32 767 Zeichen / Linux `MAX_ARG_STRLEN`. Zweitbegründung: Prompt-Leck in `/proc/<pid>/cmdline`. Test + CHANGELOG `### Added`.

**AP2 · CAR-Repo · S · parallel zu AP1** — `Clean context: share refreshable credential files by link, not by copy`. Portierung von `backend/Features/Cli/Execution/CleanContextPreparation.cs:262-303` (Hardlink → Symlink → Copy-Fallback) nach CAR; `CleanContextSpec` unterscheidet `LinkedSeedFiles`/`CopiedSeedFiles` (Claude: `.credentials.json` verlinkt, `settings.json` kopiert; Codex: `auth.json` verlinkt, `config.toml` kopiert). Test: Schreiboperation im Temp-Home schlägt in der Quelldatei durch. CHANGELOG `### Fixed`.

**AP3 · Studio · M · koordinierter Change (Server + Runner)** — `Ausführungs-Spec auf der Leitung`. `cliType`, `model`, `thinkingLevel`, `permissionMode`, `contextMode` additiv in den Claim, serverseitig aus derselben Quelle gespeist wie lokal (`ProjectRunner.cs:2240-2241,2542,2647`), runner-seitig in `RunnerOptions` als Fallback statt als Wahrheit. Ohne AP3 ist die gesamte Kette ein reiner Transporttausch.

---

## 8. Risiken und offene Punkte

| Risiko | Wirkung | Gegenmittel |
|---|---|---|
| CAR process-scoped clean home used for a resumed local or remote task | Resume state moves to a different home between attempts | Both consumers acquire the shared `TaskCleanContextStore` home and pass it through `ContextMode=shared` plus environment overrides; track `public-clean-context-lease` |
| Klartext → `stream-json` remote | `SentinelScanner` wechselt vom Rohtext- auf den `result`-Frame-Zweig | Parity P1/P5 gegen dieselbe Fixture in beiden Formen |
| Erstmalige Permission-Flag-Injektion remote | Bypass-Modus wo bisher die Host-Config galt | gewollt (Zielbild-Konzept), aber als Verhaltensänderung dokumentieren; per RunSpec steuerbar |
| Doppelte Log-Schreibung (CAR + Worker) | Plattenwachstum auf `agent-runner-01` | `IRunLogPathProvider` auf das Worker-Verzeichnis lenken; CAR-D beseitigt es (Log-Bloat hat schon einmal einen Push still blockiert) |
| Antigravity/`agentapi` protocol divergence | A CAR switch could silently change conversation, output or permission semantics | T2 keeps Antigravity on the explicit legacy adapter; revisit only with protocol parity evidence |
| No local reattach | A backend restart interrupts local runs | Keep the PID-identity-guarded reaper and recovery flow; do not claim detached-worker durability for local CAR runs |
| `remote-runner-onboard.sh` installiert das falsche Paket | Onboarding schlägt fehl | in T4 korrigieren oder an AGT-2132 hängen |
| Offene Zielbild-Entscheidungen (E1/E2/E6, Token-Refresh) | blockieren die **Container**-Tranche | blockieren die Konvergenz **nicht** — deshalb steht die Kette davor |

---

## 9. Definition of Done der Kette

1. Genau **eine** CLI-Ausführungsschicht im Repo; der Guard-Test aus T3 ist grün und nachweislich scharf.
2. Beide Deployables beziehen CAR über denselben, exakt gepinnten PackageReference; ein Test vergleicht die Pins.
3. Ein Remote-Lauf und ein lokaler Lauf verwenden das in der **Karte** gewählte Modell, Thinking-Level und den Permission-Modus.
4. Reattach nach Daemon-Neustart, Kill, Timeout, Salvage und das Envelope-Trio sind je einmal über den neuen Pfad belegt.
5. ADR-0067 existiert und ist verlinkt; die Target-Architecture beschreibt Ist = Soll; `execution-model-shift` §5 und E5 stehen auf „erledigt".
6. Die Container-Tranche kann anfangen, ohne eine Zeile Ausführungscode zu duplizieren — der Spawner-Hook liegt an einer Stelle, die das Produkt tatsächlich durchläuft.
