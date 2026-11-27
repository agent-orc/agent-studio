# Studio releases

Operations links to Releases at `#/projects/{project}/studio-releases`. This is
history for the running Studio installation's source project, not a release
list for arbitrary managed applications. Existing deploy dashboards and scripts
keep their contracts.

`GET /api/projects/{project}/studio-releases` returns the running build commit,
refresh time, pending deliveries and deployed revision intervals.
`GET /api/projects/{project}/studio-releases/cards/{key}` returns `live`,
`integrated-not-live` or `unknown` for the card detail. Both routes enforce
project access and serve a background snapshot without spawning Git.

The existing instance deploy ledger supplies successful server switches and
completed deploys. A separate application-owned file,
`<TaskRepository>/deployments/studio-release-observations.json`, remembers running
revisions absent from that ledger. Such times are first observations, not build
or inferred deploy times. The first known revision is the history baseline;
its older delivery interval is unknown and is left empty.

Older bootstrap entries from the instance ledger use the build timestamp; the
page labels those as baseline build time, without claiming a deploy time.

Only verified integration records supply candidate delivery SHAs. Commit ancestry
assigns a card to an interval when the delivery is contained in this revision
and not the preceding one. All delivery SHAs must be contained, or the verified
integration tree must be contained (including rewritten deliveries). Commit
subjects naming a key are not membership proof. Legacy receipts with only a verification tree can prove live membership,
but cannot prove a negative or a landing interval without delivery SHAs. The
immutable running identity determines live membership even when a newer
deployment has been recorded. This also handles rollback. Unavailable or
incomplete ancestry remains unknown.

Titles are read verbatim from cards. Type is the stored bug, feature or chore,
with concept mode presented as concept. Missing metadata is labelled unavailable.
The page and the open card refresh their reads every 30 seconds.
Waiting time uses the integration record time, when available. Missing timestamps
stay unavailable. The background refresh retries every 30 seconds; its timestamp
is visible. The first refresh may wait for instance source-project discovery.
