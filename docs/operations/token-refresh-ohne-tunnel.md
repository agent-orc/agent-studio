# Konzept: CLI-Token-Refresh ohne SSH-Tunnel

> Historical concept note. The binding provider-auth contract adopted on
> 2026-09-07 supersedes its token distribution and maintenance-claim options.
> Current operations use one native login per host, real CLI status probes,
> and the host-owned browser renewal flow in
> [setup/cli-relogin-runbook.md](setup/cli-relogin-runbook.md). Credential files
> and operator-device sessions are never copied to an execution host.

**Stand:** 2026-07-28 · **Phase:** P3-Vorlauf (Umsetzungsplan-Zielbild §3.3 / §5) · **Modus:** Konzept, keine Code-Änderung
**Bezug:** AGT-2262 (SSH-Brücke abreißen), `execution-model-shift` E1/E2, `car-migration-plan` §2.2 (CAR-B) und §5
**Im Code verifiziert am 28.07.:** `runner/`, `backend/Features/{Runner,Tasks,Cli}/`, `task-server/`, `contracts/TaskServer.Contracts/`, `scripts/remote-runner-onboard.sh`, `deploy/`, `docs/operations/setup/{linux-runner-host,networked-task-server,remote-runner-persistent-connection}.md`, `docs/system/cli/supported-clis.md`

**Evidenzklassen:** `[belegt]` = im Repo mit Fundstelle nachlesbar · `[Annahme]` = plausibel, aber nirgends im Repo belegt · `[zu verifizieren]` = messbar, aber noch nicht gemessen.

---

## 0. Kurzfassung — was dieses Konzept entscheidet

1. **Der Tunnel ist nicht der Refresh-Weg.** Der automatische OAuth-Refresh läuft von der CLI direkt zum Provider über ausgehendes HTTPS; der SSH-Tunnel trägt ausschließlich den Task-Server-Verkehr (`RUNNER_SERVER_URL=http://127.0.0.1:15031`). Mit P3 fällt der **administrative** SSH-Weg weg — und genau über den läuft heute die *interaktive* Neuanmeldung (`ssh -tt … codex login --device-auth`, `remote-runner-onboard.sh:243-247`) `[belegt]`. **Der Knoten ist die Re-Anmeldung, nicht der Refresh.** Die Formulierung in Umsetzungsplan §5 ist entsprechend zu präzisieren.
2. **Die Verteilungsfrage ist bereits entschieden und richtig entschieden:** D5 — jeder Host meldet sich selbst an, Credential-Dateien werden nie kopiert (`linux-runner-host.md:182-203`) `[belegt]`. Was fehlt, ist nicht der Weg der Credentials, sondern **Erkennung** und **Re-Anmeldung ohne SSH**.
3. **Erkennung ist heute rein post-mortem** — und im Backend für Coding-Karten zusätzlich wirkungslos: der Capability-Pause-Gate greift nur auf der Review-Claim-Route (`V1ReviewPlaneEndpoints.cs:218`), die Coding-Claim-Route (`/api/runner/claim`) kennt gar keinen Capability-Check `[belegt]`.
4. **Die präventive Mechanik existiert schon** — im eigenständigen Task-Server: eine benötigte Capability, die nicht `ready` ist, blockiert den Claim (`TaskServerCapabilityStore.cs:501-512`), inkl. Stale-, Drain- und Half-Open-Canary-Logik `[belegt]`. Ein **ehrlicher `provider-auth`-Status** ist der gesamte benötigte Hebel; heute wird er blind als `ready` gemeldet, sobald das Binary im PATH liegt (`RunnerCapabilityProbe.cs:30-36, 158-170`) `[belegt]`.
5. **Empfehlung: A + D.** Credential bleibt beim Host (A, heutige Linie gehärtet), die interaktive Re-Anmeldung wird ein **Wartungs-Claim mit Device-Flow-Relay über die Task-API** (D). **B (zentraler Broker) wird abgelehnt** — er verteilt genau die Refresh-Token-Lineage, die D5 abgeschafft hat. **C (API-Key)** bleibt Notausgang mit Kosten-/Plan-Implikation.
6. **Ohne P3 sofort vorbereitbar:** ehrlicher Auth-Status in der Advertisement, Coding-Claim-Gate im Backend nachziehen, CAR-B (Link statt Kopie), Re-Login-Runbook, `WaitForCapabilityRecovery` mit einem Konsumenten versehen.

---

## 1. Ist-Analyse

### 1.1 Wer hält heute welche Anmeldung

| Ausführungsort | CLI | Anmeldeweg | Träger | Refresh schreibt nach |
|---|---|---|---|---|
| Studio (Backend-lokal) | Claude | Operator-Login am Arbeitsplatz | `~/.claude/.credentials.json` | dieselbe Datei — im Clean-Mode **per Link** geteilt (`CleanContextPreparation.cs:107-111, 194-208`) `[belegt]` |
| Studio (Backend-lokal) | Codex | Operator-Login | `~/.codex/auth.json` | dito (`:119-123`) `[belegt]` |
| `agent-runner-01` (Remote) | Claude | `claude auth login --claudeai` **auf dem Host**, alternativ `claude setup-token` | Host-Home des Runner-Users | dieselbe Datei; Host-Home muss beschreibbar bleiben (`linux-runner-host.md:194-210`) `[belegt]` |
| `agent-runner-01` (Remote) | Codex | `codex login --device-auth` **auf dem Host** | `~/.codex/auth.json` des Hosts | dito `[belegt]` |
| Container (Zielbild) | beide | keiner — Token wird injiziert | per-Run-Config-Home wird gemountet (E2) | **offen**: Rückschreibpfad ist Teil dieses Konzepts |

Die Review-Rolle ist bewusst credential-arm (`RUNNER_REVIEW_CREDENTIAL_ENV`-Allowlist; keine schreibfähigen Provider-Credentials, `linux-runner-host.md:62-64`) `[belegt]`.

### 1.2 Refresh-Verhalten — der Link-statt-Kopie-Invariant

Beide CLIs erneuern ihr Token **in der Credential-Datei selbst**:

- **Studio:** Clean-Context verlinkt `.credentials.json` / `auth.json` (Hardlink Windows, Symlink sonst) und kopiert nur `settings.json` / `config.toml`; ohne das entsteht das „OAuth-Token-Roulette" von 2026-07-10 (`CleanContextPreparation.cs:238-305`, `supported-clis.md:165`) `[belegt]`.
- **CAR:** kopiert heute noch → **CAR-B ist blockierend** für jeden Clean-Mode; auf `agent-runner-01` ist der Host-Token die einzige Anmeldung (`car-migration-plan.md` §2.2, §8) `[belegt]`.

**Für den Container gilt derselbe Invariant und ist ungelöst:** Bind-Mount einer Datei plus In-Place-Rewrite durch die CLI (rename statt write) ist der klassische Bruchpunkt `[zu verifizieren — Parity-Test P18]`.

### 1.3 Was der Tunnel wirklich trägt

```
[Studio, Windows]                          [agent-runner-01, Hetzner]
 Task Server 127.0.0.1:5031  ◄── ssh -R ──  127.0.0.1:15031   (Claim/Lease/Logs/Completion)
                                            claude|codex ──────────────► api.anthropic.com / openai
                                                     (Refresh: direkt, ohne Tunnel)
 Operator-Browser  ◄── ssh -tt / ssh -L ──  interaktiver Login   ← DIESER Weg fällt mit P3 weg
```

Der Runner ist ausgehend-only und pollt (Claim 5 s; Capability-Advertisement 60 s, `RemoteRunnerDaemon.cs:180`) `[belegt]`. Jede künftige Lösung sollte diesen Kanal benutzen statt einen zweiten aufzumachen.

### 1.4 Ablauf-Fristen — was belegt ist und was nicht

| Aussage | Klasse |
|---|---|
| Token laufen ab und werden im Betrieb automatisch erneuert | `[belegt]` — der Link-statt-Kopie-Mechanismus existiert nur deshalb |
| Ein fehlgeschlagener Refresh trifft **jeden** Lauf identisch, bis zentral neu angemeldet wird | `[belegt]` `RunOutcomePolicy.cs:253-274` (17 Karten am 10.07. verbrannt) |
| Konkrete Gültigkeitsdauer der Access-/Refresh-Token | `[Annahme]` — nirgends im Repo |
| `claude setup-token` erzeugt einen *langlebigen* Headless-Token | Formulierung `[belegt]` (`:196-198`), Lebensdauer `[zu verifizieren]` |
| Interaktive Re-Anmeldung gelegentlich unvermeidbar (Browser/Device-Code) | `[belegt]` (Onboarding-Ablauf), Frequenz `[Annahme]` |
| Ablaufzeitpunkt aus der Credential-Datei auslesbar (`expiresAt`) | `[Annahme]` — kein Code liest heute den Inhalt |

---

## 2. Zielbild-Optionen

### A · Credential bleibt beim Host (heutige Linie, gehärtet)
Je Host eigene Anmeldung, eigener Refresh-Token; im Container-Modell zusätzlich ein **Credential-Volume je Host** als Quelle des per-Run-Config-Home. **Vorteil:** null neue Vertrauensbeziehung, Widerruf lokal, passt zu E2. **Preis:** löst die interaktive Re-Anmeldung allein nicht. **Verdikt: Basis.**

### B · Zentraler Credential-Broker im Control-Plane
Kann Token nicht kurzlebig *prägen*, nur **weiterreichen** — dasselbe Konto an N Hosts = exakt die Lineage-Drift, wegen der D5 eingeführt wurde (`linux-runner-host.md:186-192`) `[belegt]`. Neues Hochwertziel, Blast-Radius „alle Hosts". **Verdikt: abgelehnt.** Zulässige Restform = D (vermittelt den Vorgang, speichert nichts).

### C · API-Key statt OAuth
Env-basiert, kein Ablauf, kein Rückschreibpfad; der Code trägt es bereits (`supported-clis.md:163`, `ClaudeInitContextParser.cs:24-25` meldet die Quelle) `[belegt]`. **Preis:** Abo-Pauschale vs. verbrauchsabhängiges API-Billing bei ~98 % Token im Coding `[zu verifizieren]`; Quota-Anzeige degradiert (`supported-clis.md:200`). **Verdikt: bewusster Sonderfall/Notausgang.**

### D · Device-Flow-Relay über die Task-API (ohne SSH)
Der Ablauf existiert — nur der Transport ist heute SSH (`remote-runner-onboard.sh:225-249`). **Zielform:** ein **Wartungs-Claim** (eigener Claim-Kind, normale Lease/Heartbeat/LogShipper, kein Worktree, kein Push): Runner startet den Login lokal, **shippt URL + Einmalcode als Log-Zeilen** in die Karte, pollt bis zur Autorisierung. Live-stdin unnötig (Device-Flow pollt den Provider). **Preis:** neuer Claim-Kind + Autorisierung, wer ihn auslösen darf. `[zu verifizieren: bietet die installierte Claude-Version einen Device-Code-Login; sonst `setup-token` als headless-Ersatz]`

| Option | Interaktion ohne SSH | Cross-Host-Drift | Blast-Radius | Bauaufwand |
|---|---|---|---|---|
| A Host-Credential | nein | keine | 1 Host | keiner (Ist) |
| B Broker | ja | **hoch** | alle Hosts | hoch |
| C API-Key | entfällt | keine | 1 Key, breit | gering |
| D Device-Relay | **ja** | keine | 1 Host, 1 Vorgang | mittel |

---

## 3. Ablauf-Erkennung und Alarm

### 3.1 Was existiert

| Baustein | Fundstelle | Wirkung |
|---|---|---|
| Nachträgliche Textheuristik (401/unauthorized) | `RunnerCapabilityProbe.cs:126-135` | erkennt **nach** dem Fehlschlag |
| `provider-auth:<cli>`-Meldung | `RemoteTaskRunner.cs:578-603` → capability-failures-Route | best-effort Telemetrie |
| Schwelle + Cooldown | Backend `V1ReviewPlaneEndpoints.cs:1135-1170`, Task-Server `TaskServerCapabilityStore.cs:202-216` | ab 2 Fehlern draining |
| Claim-Sperre bei Draining | Backend **nur Review**; Task-Server **alle Claims** (`:501-512`) | Lücke 2 |
| Typisiertes Outcome `WaitForCapabilityRecovery` | `ExecutionOutcomeContracts.cs` | Lücke 3 |
| Lokaler AGT-2066-Breaker | `RunOutcomePolicy.cs:253-274` | Stopp ohne Retry-Verbrauch, Orchestrator-Nachricht High |
| Anzeige | `frontend/…/capability-health/` | Panel — Pull, kein Alarm |

### 3.2 Fünf Lücken

1. **`provider-auth` blind `ready`** (nur PATH-Check, `RunnerCapabilityProbe.cs:30-36`).
2. **Kein Capability-Gate auf der Coding-Claim-Route** — genau dort werden Karten verbrannt.
3. **`WaitForCapabilityRecovery` ohne Konsument** (nur geloggt, `RemoteTaskRunner.cs:655`).
4. **Completion kennt die Auth-Klasse nicht** (`LeaseEndpoints.cs:685-697`) — der Breaker gilt faktisch nur lokal.
5. **Kein Vorwarnsignal**; `provider-auth` nicht in `WholeHostCapabilities`.

### 3.3 Dreistufige Sensorik auf vorhandener Mechanik

| Stufe | Signal | Wirkung | Kosten |
|---|---|---|---|
| **S1 · Vorwarnung** | Ablaufzeit aus Credential-Datei; Status `ready` → `expiring` | Alarm + Runbook-Hinweis | Dateilesen `[Format zu verifizieren]` |
| **S2 · Aktive Probe** | `claude auth status --text` / `codex login status` idle + beim Start | `unavailable` → Claim-Admission blockt **bevor** eine Karte gezogen wird | Sekunden, nur idle |
| **S3 · Reaktion** | vorhandene ProviderUnauthorized-Meldung | Draining + Cooldown + Half-Open (gebaut) | keine |

### 3.4 Alarmweg

`OrchestratorMessageKind.AuthRefreshFailed` → Orchestrator-Chat (Priorität High) existiert lokal (`HumanReviewEscalation.cs:108`). **Vorschlag:** Capability-Übergänge `ready → expiring/unavailable/draining` erzeugen dieselbe Nachricht mit Host-Bezug — ein Kanal statt zweier Halbwege; bei S2 wird die Karte **nicht gezogen** statt gezogen und verbrannt.

---

## 4. Empfehlung und Migrationspfad

**A als Basis, D als Re-Anmeldeweg, C als dokumentierter Notausgang, B verworfen.**

| Phase | Steuerkanal | Credential-Ort | Re-Anmeldung | Erkennung |
|---|---|---|---|---|
| **Heute** (Tunnel) | ssh -R + Task-API | Host-Home | ssh -tt + Device-Code | post-mortem, Coding ungebremst |
| **Container-Phase** (nach T2) | unverändert | Host-Credential-Volume → per-Run-Home | wie heute + Runbook | S1+S2 aktiv, Coding-Gate greift |
| **P3** (kein Tunnel) | nur Task-API (`rnr.*`-Enrollment) | unverändert Host | **Wartungs-Claim mit Device-Relay** | Alarm via Orchestrator-Chat |

**Ohne P3 sofort startbar (kartengroß):**
1. Ehrlicher `provider-auth`-Status in der Advertisement (echte Auth-Probe statt OnPath).
2. **Coding-Claim-Gate im Backend nachziehen** (TryGetCapabilityPause auch auf `/api/runner/claim`) — größter Schadensverhinderer.
3. **CAR-B** (Link statt Kopie) — läuft bereits als CAR-10.
4. `WaitForCapabilityRecovery` mit Konsument: Karte zurück auf 2-ready statt Fehlschlag, kein Retry-Budget.
5. Runbook „CLI-Neuanmeldung auf einem Execution Host".
6. `apiKeySource` aus dem init-Frame in den Run-Record (Messbarkeit für C).
7. P18-Test scharf (Container-Mount-Durchgriff) vor Produktiv-Container-Läufen.

Der Wartungs-Claim (D) gehört **nach** T2 — nicht in die CAR-Kette hineinziehen (`car-migration-plan.md` §5).

---

## 5. Sicherheits-Betrachtung

**Geltende Randbedingungen:** Credentials nie ins Repo/Image (`.dockerignore`, `runner/Dockerfile:22-28`); kein Push-Recht im Container (E2); Secrets nie in Kommandozeile/Task-Text/Evidence; Review-Unit credential-arm — alles `[belegt]`.

| Option | Scope | Ablage | Blast-Radius |
|---|---|---|---|
| A | 1 Host, 1 Konto-Zweig | Host-Home/Volume, per-Run verlinkt | 1 Host; Widerruf = Re-Login dort |
| B | **alle** Hosts | Control-Plane + jeder Host | Flotte + Broker als Ziel |
| C | 1 Key, oft breit | Env/Secret | alles, was der Key darf — Scoping + Kostenlimit Pflicht |
| D | kein Geheimnis auf der Leitung | — | Missbrauch des *Auslösens* → Operator-Autorisierung + Audit |

**Zusatzregeln für D:** Device-Code/URL nur in den Log-Kanal, nie in `results/`; eigener Enrollment-Scope, damit ein kompromittierter Runner-Token keine Anmeldevorgänge auslösen kann.

---

## 6. Offene Punkte (`zu verifizieren`, bevor gebaut wird)

1. Struktur/Ablauffeld der Credential-Dateien beider CLIs (S1-Voraussetzung).
2. Lebensdauer von `claude setup-token` vs. interaktivem Login.
3. Device-Code-Login der installierten Claude-Version (sonst `setup-token` in D).
4. Schreibdurchgriff durch die Container-Mount-Grenze (P18).
5. Kosten-/Vertragslage Abo vs. API-Billing für C.
6. Soll `provider-auth` whole-host-drainend werden?

## 7. Abnahme dieses Konzepts

1. Ein Host mit absichtlich ungültiger Anmeldung **zieht keine Karte** — Beweis in der Claim-Antwort, nicht im Nachhinein im Log.
2. Übergang `ready → unavailable` erzeugt genau eine Operator-Nachricht mit Host-/Provider-Bezug.
3. Re-Anmeldung ist **ohne SSH-Sitzung** durchführbar und in einer Karte dokumentiert.
4. Nach der Re-Anmeldung nimmt der Host ohne Neustart wieder Karten an (Cooldown/Half-Open greift).
5. In keinem Artefakt, Image oder Repo-Stand liegt ein Provider-Credential.
