# Usage cockpit

The Studio footer is the only persistent subscription usage display. It reads `GET /api/usage/cockpit` through `UsageCockpitService`, once per selected workspace, and shows one entry per provider with reported quota windows plus today's USD ledger cost. Five execution hosts using one account still produce one provider entry. The Studio header is one navigation row (`StudioNavigationHeaderComponent`) with new-task defaults, Settings, and the account menu at the right.

## Data and presentation

- `UsageCockpitService` owns polling, workspace switching, failed-read state, and the last good snapshot. The footer and `UsageDetailSurfaceComponent` consume the same signal.
- Footer provider entries show reported weekly and session values. A window that has never been reported is omitted in the compact footer and remains `N/A` in detail. This avoids repeating `Session N/A` on every provider without turning an unknown reading into `0%`.
- Providers with no reported window and no active route use no footer space. They remain in the detail's provider list. Unknown cost is `N/A`, never `$0.00`.
- A stale value carries a visible age. Its tooltip gives the read or probe failure reason and tells the operator where to check the provider connection. The detail carries source timestamps, window resets in local time and UTC, and reasons for partial or unavailable data.
- `UsageAlarmStateService` ingests snapshots, latches trusted quota and budget alarms, and announces new acute alarms through the footer's `UsageAlarmStatusComponent`. Ordinary numeric refreshes and settled history stay quiet.
- Host sign-in state and accepted models live in Execution Hosts. A current invalid sign-in may appear once beside the provider entry as a link to Execution Hosts. Provider-auth notifications retain their own service behavior.
- Interactive chat usage remains reachable from the usage detail's text toggle. It is a separate runtime counter, requested only while that detail is open.

## Detail

`UsageDetailSurfaceComponent` is one native `<dialog>`. Desktop opens a nonmodal popover anchored to the footer trigger. Phone width opens a bottom sheet. The shared panel lists every provider, including unavailable providers, all quota windows, current model routes, today's and this week's cost, and ledger links. It also gives the full account quota context without splitting an account's allowance among hosts or models.

The Capacity section reads `GET /api/operator/capacity` only while the detail is open and lists the Ready cards whose configured model would fall back, wait or be held at the current quota, with the target route, the reason and the reset. A configured model that the installed CLI's catalog marks unavailable is flagged `Not offered`; the card model badge uses the same flag instead of "update available". `capacity-detail.util.ts` owns that view.

Percentages mean quota used, keep at most one decimal, and are never clamped. Each window also states what is left, derived from the same rounded value so the two add up to 100; at or above 100% used it reads `None left`. An unreported window remains `N/A` in detail. Cost comes from the USD ledger projection rather than from quota percentages. `usage-chip.util.ts` and `usage-detail.util.ts` own these format and detail rules.

The historical header placement and its supersession are recorded in [the Dossier](../../../../../docs/header-usage-cockpit/README.md).
