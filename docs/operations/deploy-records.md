# Report an installation deployment

Record one attempt from the deploy chain, including attempts that stop before the switch or roll back. The API retains all records in `<TaskRepository>/deployments/deploy-records.json`.

The operator audit stream records the authenticated actor. For a history change, `requested` identifies the attempt and `reported` confirms that the history write completed. If only `requested` appears, read the history to check what was saved before retrying. An import that skips every row emits a single `reported` entry with zero imported.

The two writes use the quota-reserve permission class, `workspace.settings.write`. A workspace-scoped operator API token (the `*` project grant) may also write these reports. Reading the installation history requires `workspace.operate` on the workspace. For the credential and calls that publish the live release, see [instance deployment reporting](instance-deploy-records.md).

## Open, add steps and close

The following shell example reports a rollback. Its timestamps, host names and durations illustrate a chain's observations. Set `STUDIO_URL` to the installation's HTTPS base URL and `OPERATOR_TOKEN_FILE` to its operator API token file. The helper passes the header to curl on standard input. Each call sends a complete snapshot, including the observations from earlier calls. Keep the latest snapshot in the chain's journal so the chain can resume after the API restarts.

```sh
records="${STUDIO_URL:?}/api/operations/deploy-records"
post() {
  printf 'Authorization: Bearer %s\n' "$(cat "${OPERATOR_TOKEN_FILE:?}")" |
    curl --fail-with-body --silent --show-error -X POST "$1" \
      -H @- -H 'Content-Type: application/json' --data-binary "$2"
}

# Open when the chain starts.
open='{"id":"example-switch-01","startedAtUtc":"2026-11-27T06:00:00Z",
  "fromCommit":"bb71e34b1c47edcba7eff6aefaa075552b288bca",
  "toCommit":"df3a3605cf421eb0b8343ecc58f24af99f934a1f","version":"example-2"}'
post "$records" "$open"

# Add the observed steps. Repeat with the cumulative steps as the chain progresses.
steps=$(jq -c '.steps = [
  {name:"build",startedAtUtc:"2026-11-27T06:00:00Z",endedAtUtc:"2026-11-27T06:04:00Z",outcome:"succeeded"},
  {name:"switch",startedAtUtc:"2026-11-27T06:04:00Z",endedAtUtc:"2026-11-27T06:05:00Z",outcome:"failed",note:"Health check timed out."}
]' <<EOF_JSON
$open
EOF_JSON
)
post "$records" "$steps"

# Close after the previous API passes its health check.
closed=$(jq -c '. + {endedAtUtc:"2026-11-27T06:12:00Z",outcome:"rolled-back",
  outageSeconds:480,machines:["server-a"],components:["api"],note:"Previous API restored."}
  | .steps += [{name:"rollback",startedAtUtc:"2026-11-27T06:05:00Z",
    endedAtUtc:"2026-11-27T06:12:00Z",outcome:"succeeded"}]' <<EOF_JSON
$steps
EOF_JSON
)
post "$records" "$closed"
```

Use one stable `id` per attempt, its actual `startedAtUtc`, and the full target commit SHA. Send timestamps with an explicit UTC offset. Supply the full `fromCommit`, version, steps, machines, components, note and outage duration when observed. Omit an unknown value or send `null`. Derive outage seconds from the observed loss and recovery of service. If either boundary is missing, leave the duration null. Send an empty array or zero only when the chain measured that result. Each step needs a name and can carry its start, end, outcome (`running`, `succeeded`, `failed` or `skipped`) and note.

Close with `endedAtUtc` and one of `succeeded`, `rolled-back` or `stopped-before-switch`. An open record reads as `running` until two hours from its reported start, then as `unknown-outcome`. Its end stays null until the chain reports one. A late close works. Reposting an open snapshot after a close returns HTTP 409.

## Import historical attempts

POST a JSON array of the same snapshots. Use the time the attempt actually started and its full target SHA. The importer compares start instants across UTC offsets and ignores SHA case. An existing pair is skipped, including duplicates within the array or an attempt already reported by the chain. A new pair that reuses an existing ID returns HTTP 409. A validation error returns HTTP 400. Either refusal leaves the entire batch unapplied.

```sh
history='[{"id":"example-archive-01","startedAtUtc":"2026-11-26T02:10:00Z",
  "endedAtUtc":"2026-11-26T02:17:00Z","fromCommit":"c3395e7f34d286469905d7508e0db96b2def4e4e",
  "toCommit":"bb71e34b1c47edcba7eff6aefaa075552b288bca","outcome":"succeeded",
  "note":"The duration journal recorded start, finish and commits."}]'
post "$records/import" "$history"
```

The response counts `imported` and `skipped`. A replay of this one-row import returns `{"imported":0,"skipped":1}`. Use the single-record POST to correct an existing row.

## Read the history

```sh
printf 'Authorization: Bearer %s\n' "$(cat "$OPERATOR_TOKEN_FILE")" |
  curl --fail-with-body --silent --show-error -H @- "$records?offset=0&limit=25"
```

The response contains `items`, `total`, `offset` and `limit`. Results sort by start time descending, then by ID ascending. Paging defaults to offset 0 and limit 25. The maximum page size is 100.

On a write, the API reads the source repository identified by the existing instance deployment service (`InstanceDeploy:SourceProject` can select it explicitly). `cardsInRange` lists distinct card keys from integration subjects in `fromCommit..toCommit`, including the target and excluding the base. A successful deploy's `cardsShipped` contains those keys. A rollback or stop ships no lasting change. While the outcome is open or unknown, `cardsShipped` is null. When the source project, base or full Git history is unavailable, `cardsInRange` is null and `cardRangeDetail` describes what to supply. Repost the snapshot once that evidence is available.
