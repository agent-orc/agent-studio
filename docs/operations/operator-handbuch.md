# Operator-Handbuch

Zwölf Rezepte aus dem Betrieb am 26. und 27. November 2026. Uhrzeiten sind in UTC angegeben. Die Beispiele nennen die damals wirksamen Einstellungen. `not recorded` kennzeichnet einen Schritt, für den das Protokoll keinen vollständigen Aufruf enthält.

## 1. Eine Karte wartet auf die Integration

**Signal.** Die Karte steht in Auto Review mit `integration queue position n of 7`. Spätere Lieferungen werden bereits integriert.

1. Vor jedem Neustart den Wartegrund in der Integrationsqueue und den Beleg `integration-conflict-recovery.json` lesen (Aufruf: `not recorded`).
2. Beim Status `RecoveryQueued` die reservierende Karte, ihre Konfliktdateien und den Zeitpunkt ihrer ursprünglichen Lieferung ermitteln. Später gelieferte Karten, die dieselben Dateien ändern, warten auf diese Konfliktrunde.
3. Die Integration der reservierenden Karte verfolgen. So ließ sich die Wartezeit auf `pipeline.md` bis zur Integration von AGT-4173 erklären.

**Prüfung.** Um 11:39 wurde AGT-4173 integriert. Damit endete ihre Reservierung auf `pipeline.md`. Wartet eine Karte weiter, die übrigen Dateiüberschneidungen prüfen.

**Fehler vermeiden.** Der Operator deutete den HTTP-Timeout der Engine nach 60 Sekunden und den Zustand `applying` als verlorene Entscheidung. Der API-Neustart ließ alle sechs Karten weiter warten. Ein alter Post-Merge-Claim blockierte danach neue Läufe mit HTTP 409.

## 2. Der Integrationsbranch ist rot

**Signal.** Für `develop` liegt eine rote Meldung vor oder mehrere Karten scheitern am selben Test. Ein abgelaufener Claim kann ebenfalls eine rote Meldung erzeugen.

1. Den betroffenen Commit auf dem ruhigen Gate-Host vollständig mit `fastpath-verify-full.sh` prüfen (Argumente: `not recorded`). Der Hilfslauf `verify-loop.sh` prüfte neue Stände und schrieb nach `/home/agent/fastpath/loop-summary.log`. Bei einem Hänger lieferte die Option `--blame-hang-timeout 6m` den Testnamen.
2. Für jeden fehlgeschlagenen Test eine präzise Reparaturkarte mit Commit, Testname und beobachtetem Fehler anlegen. Zwei Beispiele aus derselben Schicht waren der Hänger in `BatchGateFallbackStallTests.Stalled_gate_flight_that_honours_its_cancellation_resumes_with_the_start_charged` und die fehlgeschlagene Prüfung in `TaskIndexCacheTests.TenConcurrentBoardReads_DuringASlowRefresh_AllReturnAtOnce`. Dafür entstanden AGT-4249 und AGT-4250. Automatisch erzeugte Karten ohne Testnamen ins Backlog legen (Aufrufe: `not recorded`).
3. Fehler unter Last und in Isolation vergleichen. Nach Integration der Reparaturen den neuen Commit wieder vollständig prüfen. Bis zum sauberen Ergebnis den Deploy zurückhalten.

**Prüfung.** Für `497d40ebb` meldete der Gate-Host 18.532 bestandene und 15 übersprungene Backend-Tests, 75 bestandene Engine-Tests, grüne Guards und grünes Lint. Alle 5.704 Frontend-Tests bestanden in drei Reihenfolgen.

**Fehler vermeiden.** Der Operator hatte vier Karten nach grüner Einzelprüfung akzeptiert. Erst der volle Lauf auf dem gemeinsamen Stand zeigte die Regressionen. Eine Reparaturkarte ohne Testnamen liefert keinen Ansatz zur Nachprüfung.

## 3. Eine geprüfte Version ausrollen

**Signal.** Ein neuer Commit soll auf Server, Runner und Review-Containern live gehen.

1. Vor den Builds den Commit in den Verifikations-Checkout holen. Der protokollierte Aufruf enthält den Platzhalter für den Ziel-Commit.

   ```bash
   su - agent -c 'git -C /home/agent/batch-verify2/repo fetch -q origin <sha>'
   ```

2. Auf Runner- und Gate-Host API, Web, Engine, Agent-Host und Gate-Worker für denselben Commit bauen. Builds und vollständige Verifikation dürfen parallel laufen. Die Umschaltkette erst nach grüner Verifikation starten. Für Deploy 64 sind `mk-d64.sh`, `d64-chain.sh` und das Protokoll `d64-chain.log` benannt (Build-Aufrufe: `not recorded`).
3. Mit `/root/lane-idle.sh` auf eine freie Integrations-Lane warten. Vor dem Umschalten prüft die Kette mit `/root/preflight-start.sh <image-tag> [deadline]`, ob die neue API mit den Produktionsdaten startet. Die drei Datenverzeichnisse bilden die schreibgeschützte Unterlage, Änderungen gehen in eine Wegwerfkopie. Der Lauf hat kein Netz, 12 GB Speicher und sechs CPUs. Bei `PREFLIGHT FAILED, not switched` endet der Versuch hier.
4. Den Server mit `d29-switch.sh` umschalten. Nur das erwartete Image in der erwarteten Version darf als gesund gelten. Nach drei Minuten ohne Erfolg sichert das Skript das Startprotokoll, stellt Server- und Engine-Konfiguration sowie Gate-Worker-Pfad wieder her und endet mit Code 3. Die Kette überspringt dann die Host-Installation. Nach Erfolg erst den Agent-Host aktualisieren, danach die Review-Container einzeln, sobald sie frei sind.
5. Start und Ende von Builds, Preflight, Serverwechsel, Host-Installation und letztem Review-Wechsel sowie einen Rollback in `devspace/deploy-durations.md` eintragen (Aufruf: `not recorded`).

**Prüfung.** Beim zweiten Versuch von Deploy 64 war der Preflight nach 28 Sekunden grün. Der Server wechselte um 17:46:31, die Versionsroute antwortete um 17:47:15, die Host-Dienste liefen um 17:47:37. Erst um 18:48:47 hatte auch der letzte Review-Container die neue Version.

**Fehler vermeiden.** Beim ersten Versuch blieb die API auf den Produktionsdaten beim Start hängen. Nach dem manuellen Rollback erkannte das Skript den gesunden alten Container als Erfolg und installierte den neuen Agent-Host gegen den alten Server. Ein Start auf leerem Workspace hätte diesen Fehler ebenfalls übersehen.

## 4. Der Runner nimmt bei voller Platte keine Karten an

**Signal.** Auf dem Run-Host sind 95 % belegt. In Ready liegt Arbeit, doch neue Claims bleiben aus. Der Server kann dabei eine leere Ursachenliste melden.

1. Unter `caches/preparation/.runs` die Vorbereitungskopien je Versuch untersuchen. Von 55 Kopien zu je 1,3 GB wurden nur die 40 entfernt, auf die kein Prozess mehr verwies. Als Übergangslösung lief `/root/prune-prep-runs.sh` alle 15 Minuten.
2. Testhost-Verzeichnisse unter `agentstudio/instances/<key>` getrennt prüfen. Verzeichnisse älter als zwei Tage, die **keine Datei** enthalten, zunächst durch Umbenennen in Quarantäne verschieben. Auf dem Gate-Host ist dafür `/home/agent/fastpath/quarantine-instances.py` belegt. Dort blieben 59.876 alte Verzeichnisse mit Inhalt erhalten.
3. Bei alten Builds nur eigene Überreste aufräumen. Auf dem Run-Host wurden 56 alte Build- und Quellordner unter `/home/agent/operator-verification` sowie alte Quellbäume unter `/root/studio-src` und `/root/engine-src` entfernt. Live- und Rollback-Stand behalten. Vor dem Löschen der Quarantäne erneut auf enthaltene Dateien prüfen (Aufrufe: `not recorded`).
4. Eine noch laufende Schatteninstanz nach den Aufnahmen beenden und ihre Schreibschicht entfernen. Sie hatte in 35 Minuten weitere 7,8 GB belegt.

**Prüfung.** Nach dem Beenden der Schatteninstanz und dem Aufräumen der Vorbereitungskopien sank die Belegung auf 82 %. Um 23:12:57 nahm der Runner wieder Karten an.

**Fehler vermeiden.** Alter allein rechtfertigt keine Löschung. Die zurückbehaltenen Testhost-Verzeichnisse enthielten 61 GB an Resten von Einmalaufträgen. Auch die Schreibschicht des Operators gehörte zur Ursache.

## 5. Die Quotensteuerung hält Erstläufe zurück

**Signal.** Ready enthält startfähige Karten, Slots sind frei und die Zulassung meldet `outcome=Wait` mit einer Projektion bis zum Reset.

1. Die Kappe mit `GET /api/cli/quota/caps` lesen. Nach den ersten 5 % des Zeitfensters gilt die Linie „Kappe × verstrichener Anteil des Fensters“. Erreicht oder überschreitet der Verbrauch diese Linie, wartet ein Erstlauf ohne zugelassenen Ersatzanbieter. Bei einer Wochenkappe von 99 % lag die Linie um 11:40 bei 93 %, der Verbrauch bereits bei 94 %.
2. Vor einer Umleitung den Kartenzustand und die Art des Laufs lesen (Aufruf: `not recorded`). Laufende Versuche behalten ihr Modell. Eine fortgesetzte mechanische Konfliktrunde konnte trotz Pacing starten. Ein frischer Versuch nach `MechanicalFallback` wurde wieder geprüft. `st.js route` setzt eine feste Modellbindung, mit der auch die Ausnahme für die Konfliktrunde entfällt.
3. Die Reserve für kurze Schritte getrennt von der Wochenkappe behandeln. Zehn Punkte Reserve hielten lange Arbeit schon nahe 90 % zurück. Für die angeordnete Auslastung setzte der Operator sie mit `PUT /api/model-selection/policy/reserve` von 10 auf 0 (Request-Body: `not recorded`). Zur Rückkehr zum Standard waren 10 vorgesehen. Die Kappe blieb bis zum Reset bei 99 %, für das neue Fenster galt auf Anweisung 100 % (Schreibaufruf: `not recorded`).

**Prüfung.** Gegen 20:07 stieg die Linie über den Verbrauch von 98 %. Um 20:10 waren alle vier freigegebenen Nacht-Slots belegt.

**Fehler vermeiden.** Der Operator leitete zwei vermeintlich wartende Konfliktrunden zu Claude um. Beide liefen bereits auf Codex. Erst Status und zugelassene Modelle des Hosts prüfen.

## 6. Einer laufenden Karte CPU-Vorrang geben

**Signal.** Der Run-Host hat keine freie CPU-Zeit mehr, ein Compilerlauf dauert 450 bis 550 Sekunden und eine bestimmte Karte hat einen Termin.

1. Den laufenden Worker über seine Lease der Karte zuordnen. Seine cgroup liegt unter `agent.slice/agent-work.slice/agent-runner.service/worker-<lease>-*`.
2. In genau dieser cgroup `cpu.weight` für den relativen Anteil unter Last und `cpu.max` für die Obergrenze einstellen (Schreibaufrufe: `not recorded`). Für AGT-4248 mit Termin 06:13 setzte der Operator Gewicht 1000 und sechs Kerne, für AGT-4198 Gewicht 400 und drei Kerne.

**Prüfung.** Beide Werte am Worker der jeweiligen Lease ablesen und mit dem Soll vergleichen. Bei 18 Slots lag der Standard je Worker bei Gewicht 100 und 1,04 Kernen. Leseaufruf und Laufzeitgewinn: `not recorded`.

**Fehler vermeiden.** Zusätzliche Slots dienten in dieser Schicht Aufgaben ohne Build. Bei ausgelasteter CPU geben zusätzliche Slots der dringenden Karte keinen Vorrang.

## 7. Review-Aspekte auf zwei Anbieter verteilen

**Signal.** Die Reviews verbrauchen Claude-Kontingent, während Codex Kapazität hat. Für die Anforderungserfüllung soll ein zweiter Anbieter mitlesen.

1. Die bisherige Zuordnung sichern. Die Schicht verwendete `review-steps-snapshot-1127.json`. Für den Rückweg nennt das Protokoll `st.js review-model` (Argumente und Aufruf zum Sichern: `not recorded`).
2. Codequalität, Tests und Nachweise sowie Dokumentationswirkung auf Codex `gpt-6.2-astra` mit Stufe `high` legen. Anforderungserfüllung und Gesamtnote auf Claude Opus 5.5 mit Stufe `low` belassen (Schreibaufruf: `not recorded`).

**Prüfung.** Im ersten abgeschlossenen Review, AGT-4235, liefen die drei umgestellten Aspekte auf Codex mit je rund 18.000 Eingabetokens in 8 bis 11 Sekunden. Die Anforderungserfüllung prüfte Claude Opus. Das Review endete mit `pass`.

**Fehler vermeiden.** Ein erfolgreicher Gesamtstatus allein bestätigt die Zuordnung der Aspekte nicht. Die Anbieter im tatsächlichen Lauf einzeln nachlesen.

## 8. Eine Abnahme begründen und Ablehnungen bearbeiten

**Signal.** Eine integrierte Karte oder ein Forschungsergebnis wartet auf Abnahme. Ein Review-Urteil, eine offene Lücke oder ein fehlender Nachweis braucht eine Entscheidung.

1. Das Mandat prüfen. Anfangs galt `accept-with-reason` nur für benannte Karten. Am Abend übertrug der Owner die Entscheidungen ausdrücklich dem Operator. Ergebnis und Urteil lesen, die gewählte Option begründen und offene Arbeit einer konkreten Folgekarte zuordnen. Entscheidung und Grund festhalten, wie in `decisions-1127.json` und `decisions-log.jsonl` (vollständiger Abnahmeaufruf: `not recorded`).
2. Bei einer maschinellen Ablehnung den fehlenden Nachweis bestimmen.

   | Befund | Nächster Schritt |
   |---|---|
   | `requirement-fit verdict was concerns` | Den konkreten Vorbehalt lesen. Bei AGT-4139 hatte das Auswahlmuster keinen einzigen Test der betroffenen Spezifikation ausgeführt. Den offenen Nachweis festhalten. |
   | Kein lesbares, zum Lauf passendes Inhaltsurteil | Ergebnis und Laufzuordnung prüfen. Bei AGT-4155 und AGT-4170 sah die Inhaltsprüfung `results/findings.md` nicht. Bei AGT-4122 gehörte das Urteil zu einem früheren Lauf. Fünf Karten ohne lesbares Urteil gingen zur Abnahmeentscheidung an AGT-4270. |
   | `integration-dead-end` | Für die Veröffentlichung fehlt ein Verifikationsbeleg auf der First-Parent-Linie. Auf einen grünen Post-Merge-Lauf warten oder Commit, Suites, Zeiten und Log-Prüfsumme einem berechtigten Menschen übergeben. Die genannte Route `POST /api/projects/Agent Studio/operator-candidate-verifications` verlangt eine menschliche Identität. |

**Prüfung.** Den Kartenstatus mit dem Entscheidungsprotokoll abgleichen. In der ersten Abendrunde wurden 18 Karten mit Grund akzeptiert. 13 weitere blieben wegen fehlender Publikationsverifikation gesperrt.

**Fehler vermeiden.** Ein grüner Lauf auf dem Gate-Host ersetzt den fehlenden Eintrag auf der Publikationslinie nicht. Der Operator hinterlegte den Nachweis für den Owner und verwendete dessen Anmeldung nicht.

## 9. Eine Eskalation entscheiden

**Signal.** Eine Karte in der Eskalations-Lane wartet auf eine Antwort, eine korrigierte Lieferung oder eine Entscheidung über überholte Arbeit.

| Befund | Schritt |
|---|---|
| Es fehlt eine fachliche Entscheidung. | Die konkrete Frage beantworten. AGT-4182 erhielt die Freigabe zur Erweiterung auf zwei Testdateien. |
| Die Lieferung bleibt nötig und ihre Integration ist gescheitert. | Mit präzisem Auftrag zurückgeben. Bei AGT-4108 lautete er, den Integrationsbranch zu mergen und erneut zu liefern. |
| Eine neuere Karte hat die Arbeit übernommen oder die Lieferung ist überholt. | Mit diesem Grund im Backlog parken. AGT-4141 war durch AGT-4173 abgedeckt. |
| Die Fortsetzung eines Audits hat die Ergebnisse verloren. | Nach Prüfung der ursprünglichen Lieferung eine frische Karte anlegen. Für AGT-4144 und AGT-4159 entstanden AGT-4274 und AGT-4275. |

Die Aufrufe für Antworten, Zurückgeben, Parken und Neuanlage sind `not recorded`. Bereits erledigte Karten mit Abnahmegrund abschließen.

**Prüfung.** Für jede Ausgangskarte Antwort, neue Lane oder Nachfolgekarte prüfen. Um 23:20 war die Eskalations-Lane leer und die beiden neu begonnenen Audits hatten eigene Kartennummern.

**Fehler vermeiden.** Drei Fortsetzungen von AGT-4011 gingen von einem bereits ausgeführten Merge aus. Bei einem Konflikt kann der Runner den Merge der Fortsetzung überlassen. Im Auftrag ausdrücklich sagen, wer ihn ausführt.

## 10. In „Needs you“ die Entscheidungen finden

**Signal.** Die Ansicht meldet Hunderte Einträge. Aus dem Zähler geht nicht hervor, wie viele Entscheidungen jemand treffen muss.

1. Die Einträge nach Anlass und Projekt zählen. Von 783 Meldungen entfielen 239 auf Quotenhinweise, 125 auf rote, aufgegebene Batches und 233 auf das Testprojekt Runbook.
2. Bei Kartenverweisen prüfen, ob die Karte existiert. Für die verbleibenden Einträge feststellen, welche konkrete Entscheidung ansteht. Wiederholte Hinweise und verwaiste Verweise getrennt festhalten (Abfragen: `not recorded`).
3. Den Befund mit den Zahlen an AGT-4255 geben. Die gewählte Richtung für „Needs you“ war ein Arbeitsplatz für Entscheidungen.

**Prüfung.** Alle 233 Verweise des Testprojekts führten zu inzwischen fehlenden Karten. Ein bereinigter Gesamtwert nach der Produktänderung ist `not recorded`.

**Fehler vermeiden.** Den Wert 783 als Anzahl offener Entscheidungen zu melden, würde unter anderem 239 Quotenhinweise als Entscheidungsbedarf zählen.

## 11. Owner-Seiten in der Schatteninstanz ansehen

**Signal.** Für eine Sichtprüfung werden etwa Struktur und Deployment-Dashboard aus Sicht des Owners benötigt. Die Verwaltungs- und Strukturrouten der Live-Instanz weisen API-Tokens ab.

1. Für tokenfähige Seiten nennt das Protokoll `browser-qa/studio.mjs` mit `--token-file`, `--alias` und `--full-page` (vollständiger Aufruf: `not recorded`).
2. Für Owner-Seiten auf dem Run-Host `/root/shadow-studio.sh` verwenden. Es startet die Live-Version im lokalen Profil auf einer Copy-on-Write-Kopie der Produktionsdaten. Das Docker-Netz hat keine Route nach außen. Der Zugriff erfolgt ausschließlich über eine SSH-Portweiterleitung (Aufruf: `not recorded`).
3. Struktur, Deployment-Dashboard, Team oder Magazine aufnehmen. Größe und Laufzeit der Schreibschicht begrenzen und die Instanz direkt nach den Aufnahmen stoppen und entfernen (Grenzwerte und Aufrufe: `not recorded`).

**Prüfung.** Auf den Aufnahmen waren die Owner-Seiten lesbar. Das Deployment-Dashboard zeigte 17-mal „No environments declared“. Nach der Aufnahme prüfen, dass die Schreibschicht entfernt ist.

**Fehler vermeiden.** Die erste Instanz belegte nach 35 Minuten 7,8 GB. Eine persönliche Anmeldung oder die Aktivierung einer Team-Persona mit zweitem Faktor gehört nicht zur Sichtprüfung.

## 12. Ein Forschungsergebnis ein zweites Mal lesen

**Signal.** Ein Bericht braucht eine unabhängige Zweitlesung, oder sein Ergebnis-Review hat nach einem Containerwechsel keinen aktiven Bearbeiter mehr.

1. Für eine fachliche Gegenprüfung eine eigene Lesekarte mit dem ursprünglichen Untersuchungsgegenstand anlegen. Für Neustart und Nebenläufigkeit waren AGT-4158 und AGT-4159 als Zweitlesungen auf Astra angesetzt (Anlageaufruf: `not recorded`). Die Entwürfe beider Lesungen mit den vorhandenen Befunden und offenen Karten abgleichen. Für Überschneidungen bei betroffenen Dateien nennt das Protokoll `overlap.js` (Argumente: `not recorded`).
2. Fehlt lediglich das Review eines bereits gelieferten Berichts, zuerst prüfen, welcher Lauf die Ergebnisdateien enthält. Solange die ursprüngliche Lieferung aktuell ist, deren Review erneut anfordern. Dafür sind beide Schreibweisen überliefert.

   ```text
   node st.js review-results KEY
   POST /api/tasks/{id}/review-results
   ```

**Prüfung.** Das Urteil muss den Bericht und die Ergebnisse des beabsichtigten Laufs bewerten. Bei einer unabhängigen Zweitlesung die Befunde beider Karten einander zuordnen, bevor daraus weitere Karten entstehen. Zum Beispiel lieferten zwei Modelle zur Frage, wo Fehlermeldungen verloren gehen, neun und zwölf Entwürfe. Der Operator übernahm daraus drei Themen.

**Fehler vermeiden.** Eine Fortsetzung ohne Änderungsauftrag startete bei zwei Audits einen leeren Lauf. Die alten Dateien lagen noch auf dem Server, doch das spätere Ergebnis-Review prüfte den inzwischen aktuellen leeren Lauf. In diesem Fall wurden die Audits auf frischen Karten neu begonnen.
