# CLI-Versionen auf allen Hosts ausrollen

Dieser Ablauf wird vom Operator ausgeführt. Ein Versionspin im Repository
ändert bestehende Installationen nicht. Implementierte Option: bestehende
npm-Symlinks übernehmen, statt Hosts auf ein neues Verzeichnislayout festzulegen.

## Vorbereitung und Reihenfolge

1. Aus einem geprüften Release arbeiten. Die Sollversionen stehen in
   `task-server/appsettings.json`: Codex **0.166.0**, Claude **2.1.305**.
   Bei einer Versionsänderung auch `TaskServerOptions`, die Dockerfiles,
   `remote-runner-onboard.sh`, die exakte sudoers-Regel und die Review-Rezeptur
   in `deploy/networked/remote_host.py` aktualisieren.
2. Alle Identitäten inventarisieren: runner-01 Coding und Review, KORI
   `kori-review`, `kori-review-2`, `kori-review-3` sowie den API-Container.
   Je Identität Version, Installationspfad, Image-Digest und bisherigen
   Launcher-Zielpfad sichern. Keine Authentifizierungsdateien protokollieren.
3. Task Server vor Engine und Hosts aktualisieren. Siehe
   [Deploy-Drain](deploy-drain-procedure.md). Pending Pushes und laufende
   Batch-Veröffentlichungen zuerst abschließen. Coding und Review über
   `studio-op host drain <client-id>` drainen. Erst fortfahren, wenn alle
   Slots und Reviews beendet sind. Keine laufenden Review-Prozesse erzwingen.

## Bestehenden Linux-Host aktualisieren

Die alte sudoers-Regel kann den neuen Unterbefehl nicht freigeben. Deshalb
führt der Operator einmalig in seiner Root-Sitzung aus einem geprüften,
rootgeschützten Release-Verzeichnis aus:

```bash
bash scripts/upgrade-agent-runner-deploy.sh --apply
sudo -l -U agent
```

Das Skript installiert den aktuellen Deploy-Helper, seine beiden Policy- und
Validierungsdateien sowie die exakte sudoers-Regel. Es validiert mit `visudo`,
sichert vorherige Dateien unter `/var/backups/agent-runner-deploy/` und verwendet
dieselbe Sperre wie die Promotion. Es startet keine Dienste und verändert keine
Gruppen. Der Netzwerk-Deploy liefert diese Dateien im `agent_host`-Archiv mit;
der Operator prüft und entpackt dieses Archiv in sein geschütztes Release-Verzeichnis.
Diesen Bootstrap vor der nächsten Runner-Promotion ausführen. Ein Agent darf
weder den Helper noch seine sudoers-Regel selbst erweitern.

Danach als Operator, bei weiterhin gedrainten Rollen:

```bash
sudo /usr/local/sbin/agent-runner-deploy update-clis 0.166.0 2.1.305
```

Der Helper übernimmt vorhandene Symlinks in `/usr/bin` und `/usr/local/bin`.
Dazu gehört der am 17. November gesetzte Codex-Link nach
`/opt/agent-cli/releases/codex-0.166.0/`. Reguläre Wrapper-Dateien werden zur
Prüfung abgelehnt. Beide Pakete werden gemeinsam in einem neuen Prefix unter
`/opt/agent-cli/releases/` installiert. Version, Anmeldung und Modellkatalog
werden als `agent` geprüft, bevor die Launcher aktiviert werden. Der Review-Guard
verwendet bei alten Hosts auch `/opt/agent-runner/agent-host`.

Ein aktiver Review-Daemon braucht einen aktuellen Guard-fähigen Runner. Falls
dieser fehlt, zuerst den Runner bei gedrainten Rollen aktualisieren. Nicht
`--force` verwenden, um diesen Schutz zu umgehen.

## KORI und API: Container ersetzen

Die kanonische KORI-Rezeptur ist `review_dockerfile()` in
`deploy/networked/remote_host.py`, ausgeführt durch `review_build` des
Netzwerk-Deploys. Ein Dockerfile unter `/opt/agent-studio/kori-review/` ist keine
Versionsquelle mehr. Vertrag für das digestgebundene `reviewBaseImage`:
Linux mit Node/npm, Root-Buildzugriff, vorhandener Benutzer `agent` und die
bisherigen Review-Werkzeuge. Die Rezeptur installiert ausdrücklich
`@openai/codex@0.166.0` und `@anthropic-ai/claude-code@2.1.305`, prüft beide
Versionsausgaben und wechselt vor dem Runtime-Start zu `agent`. Auch ein altes
Basisimage kann damit keine alten CLI-Versionen durchreichen. Der Plan-Digest
enthält die Versionspins, sodass ein alter Buildplan nicht wiederverwendet wird.

Über den Netzwerk-Deploy neu bauen und das zurückgegebene unveränderliche
Image-Digest verwenden. Alle drei KORI-Container nach dem Drain einzeln ersetzen;
bestehende Mounts, Secrets und Identitäten beibehalten. Den API-Container aus
`backend/Dockerfile` ebenfalls neu bauen und ersetzen. Ein API-Rebuild allein
aktualisiert weder KORI noch runner-01.

## Prüfung und Wiederaufnahme

Auf jedem Host beziehungsweise in jedem Container als Runtime-Benutzer:

```bash
codex --version
claude --version
codex login status
claude auth status
codex debug models
claude models
```

Anschließend je einen echten, kleinen Modellaufruf für `gpt-6.1-sol` und
`gpt-6.1-terra` über den normalen Runner-Pfad durchführen. Das ist ein
kostenpflichtiger Operator-Smoke-Test. Im Host-Detail für jede Identität
Versionen, Pfade und frischen Prüfzeitpunkt kontrollieren. Der Runner meldet
beide CLIs über Capability-Advertisements. Der Task Server speichert den
Driftbeginn dauerhaft. Bei mehreren aktiven Identitäten auf demselben Host
bleibt die älteste gemeldete CLI-Version maßgeblich, bis alle aktualisiert sind.
Ein aktueller Coding-Runner kann einen alten Review-Runner nicht verdecken.
Nach mehr als einer Stunde unter Soll erscheint im
Host-Health-Detail ein Hinweis mit Hostname und Update-Befehl; zusätzlich kommt
ein dedupliziertes `host.cli-drift.alarm`-Feed-Ereignis. Nach einer frischen
Meldung mit erreichter Sollversion verschwindet der Health-Hinweis. Ein neuer
Sollversionswechsel startet eine neue Frist. Fehlende oder unlesbare Versionen
sind kein Nachweis, dass der Host aktuell ist; diese separat prüfen.

Erst nach erfolgreicher Prüfung jede Identität mit
`studio-op host resume <client-id>` freigeben. Ergebnis samt alten und neuen
Versionen und Image-Digests im Operator-Log festhalten.

## Rollback

Bei einem fehlgeschlagenen Neustart stellt der Helper die vorherigen Launcher
automatisch wieder her. Für einen späteren Rollback beide Rollen erneut drainen
und die vom Helper ausgegebene Root-Datei auswählen:

```bash
bash /opt/agent-cli/rollback-<timestamp>-<pid>.sh
systemctl restart agent-runner.service
/usr/local/sbin/agent-runner-deploy restart-review
```

Das Skript stellt die exakten früheren Launcher und den vorherigen `current`-Link
wieder her, einschließlich des manuellen November-Prefixes. Frühere Releases
nicht löschen. Für Container den zuvor notierten Image-Digest wieder einsetzen
und alle betroffenen Container bei gedrainten Rollen neu erstellen. Danach
Version, Authentifizierung und Modell-Smoke erneut prüfen, bevor die Rollen
fortgesetzt werden. Bei einem Helper-Rollback die zuvor gesicherten Dateien aus
`/var/backups/agent-runner-deploy/` in ihre ursprünglichen Pfade kopieren und
`visudo -c` ausführen; dabei keine alte, uneingeschränkte sudo-Regel ergänzen.
