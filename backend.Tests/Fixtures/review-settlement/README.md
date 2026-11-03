# Remote review settlement journal fixtures

Format 1 entries for `RemoteReviewSettlementJournalFormatTests` (AGT-3467).
Do not reformat or edit them: each stored hash covers the JSON members as the
writing release produced them.

- `recorded-2026-10-20-review_ac90d6599ee9427880fbce7283f4ddc8.json`: recorded
  unchanged from a production card (AGT-3279, 20 October 2026). It predates
  `artifactsReduced` (report) and `outputTail` (commands).
- `format-1-58a576eab-review_5a8a576e0b3d4c1fa9e2d7c6b5a40397.json`: written by
  `RemoteReviewSettlementJournal.Write` of release 58a576eab, the release before
  Deploy 32. The report is the recorded report above, round-tripped through
  the 58a576eab `ReviewReportRequest` like a 58a576eab review host sends it,
  with outcome `Pass`, every verdict `pass`, and an `IntegrationSettled`
  delivery record. It has no `outputTail` member.
