# Tunnel-loss drill report (AGT-2937, Dossier AGT-W65 D9)

All times are `synthetic-drill-time` from controlled clocks. They are not historical incident data.

| Suite | Scenario | Outage | Kind | Attempt | Fence/Epoch | Granted expiry | Stop-before | Teardown | Quarantine ref / SHA / push | Outbox key | Receipt | Terminal |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| runner | beyond-authority-stop | 2030-01-01T00:00:01Z .. - | coding | run-drill-1 | 7/0 | 2030-01-01T00:02:00Z | 2030-01-01T00:01:30Z | 2030-01-01T00:01:30Z | - / - / - | - | local-deadline-rejected | worker-stopped |
| runner | short-interruption | 2030-01-01T00:00:30Z .. 2030-01-01T00:00:45Z | coding | run-drill-1 | 7/0 | 2030-01-01T00:02:00Z | 2030-01-01T00:03:30Z | - | - / - / - | - | renewed-same-fence | running |
| runner | superseded-quarantine | 2030-01-01T00:00:01Z .. - | coding | run-drill-1 | 7/0 | 2030-01-01T00:02:00Z | 2030-01-01T00:01:30Z | 2030-01-01T00:01:30Z | agent-studio/quarantine/drill-runner/AGT-DRILL-1/run-drill-1/fence-7/065d34468d535b7e7bcb3a671a2368cc46fbbd77 / 065d34468d535b7e7bcb3a671a2368cc46fbbd77 / pushed | - | quarantined; replacement claim fence 8 | superseded-quarantined |
| runner | beyond-authority-exact-readoption | 2030-01-01T00:00:01Z .. 2030-01-01T00:07:00Z | coding | run-drill-1 | 7/0 | 2030-01-01T00:02:00Z | 2030-01-01T00:08:30Z | 2030-01-01T00:01:30Z | - / - / - | run-drill-1:1 | adopted+report-applied-once | recovered |
| runner | beyond-authority-superseded | 2030-01-01T00:00:01Z .. 2030-01-01T00:07:00Z | coding | run-drill-1 | 7/0 | 2030-01-01T00:02:00Z | 2030-01-01T00:01:30Z | 2030-01-01T00:01:30Z | - / - / - | run-drill-1:1 | stale-authority; replacement fence 8 | superseded-rejected |
| runner | lost-report-ack | 2030-01-01T00:00:00Z .. - | coding | run-drill-1 | 7/0 | 2030-01-01T00:02:00Z | 2030-01-01T00:01:30Z | - | - / - / - | run-drill-1:1 | duplicate-acknowledged | report-settled |
| task-server | server-restart-exact-readoption | 2030-01-01T00:00:00Z .. 2030-01-01T00:05:00Z | coding | run_8f57320e62a04e7ca7c58023601c8215 | 1/0 | 2030-01-01T00:02:00Z | - | - | - / - / - | completion:run_8f57320e62a04e7ca7c58023601c8215:drill | adopted; completion blocked applied once | 4-auto-review |
| task-server | review-exact-readoption | 2030-01-01T01:00:00Z .. 2030-01-01T01:11:00Z | review | rat_c325e6d708bc4f76ba5080899ce64aa8 | 1/0 | 2030-01-01T01:02:00Z | - | - | - / - / - | report-rat_c325e6d708bc4f76ba5080899ce64aa8 | adopted; report rrpt_5aaee32e1e854db68dfee52870a7383d settled once | Pass |
| task-server | server-superseded-coding | 2030-01-01T00:00:00Z .. 2030-01-01T00:05:00Z | coding | run_cd9dc659542c4ebbbef47904399cac37 | 1/0 | 2030-01-01T00:02:00Z | - | - | - / - / - | - | re-adoption invalid-state; completion rejected | superseded |
| task-server | server-superseded-coding | 2030-01-01T00:00:00Z .. 2030-01-01T00:05:00Z | coding | run_cdcef91788fc41599297efd8ffb4f1c0 | 2/0 | 2030-01-01T00:07:00Z | - | - | - / - / - | - | new admitted claim | 3-progress |
| task-server | review-superseded | 2030-01-01T01:00:00Z .. 2030-01-01T01:11:00Z | review | rat_e03f19d8f0f648ee84c082f900542b93 | 1/0 | 2030-01-01T01:02:00Z | - | - | - / - / - | - | re-adoption stale-authority; report rejected | superseded |
| task-server | review-superseded | 2030-01-01T01:00:00Z .. 2030-01-01T01:11:00Z | review | rat_e03f19d8f0f648ee84c082f900542b93 | 2/0 | 2030-01-01T01:13:00Z | - | - | - / - / - | - | explicit re-fenced reclaim | leased |

Raw evidence per scenario is retained in `tunnel-drill.jsonl`.

## Step receipts

The `integration-resume` step includes the settled-review restart proof
`Restart_before_integration_starts_integrates_afterwards_without_a_new_review`.
It resumes integration from the persisted settlement without another review attempt.
This step uses isolated backend fixtures; its receipt is a test result, not a historical outage time.

```text
runner pass Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 1 s - AgentRunner.Tests.dll (net10.0)
task-server pass Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 4 s - TaskServer.Tests.dll (net10.0)
integration-resume pass Passed!  - Failed:     0, Passed:    26, Skipped:     0, Total:    26, Duration: 8 s - OrchestratorApi.Tests.dll (net10.0)
```
