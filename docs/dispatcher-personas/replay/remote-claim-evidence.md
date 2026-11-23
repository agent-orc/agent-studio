# Remote claim evidence for the 17 November wave

Extract made on 22 November 2026 from the runner task reports
`results/status.md` of the wave cards, which the board keeps outside the
repository. Ten of the 57 wave cards have a report that names its integration
delivery ref. Every ref has the form `refs/heads/agent-studio/results/run_…/fence-…`,
which only the remote runner writes (`runner/GitWorkspace.cs`, result ref
construction). Every named delivery commit exists in this repository; check it
with `git cat-file -t <sha>` and `git log -1 <sha>`.

| Card | Delivery ref named in the report | Commit subject in git |
| --- | --- | --- |
| AGT-3894 | `refs/heads/agent-studio/results/run_8ff472c36a674eacb62576eee80f6b1d/fence-3` at `a448ae9f9` | Merge origin/develop into AGT-3894 |
| AGT-3895 | `refs/heads/agent-studio/results/run_1b0327efbccb4e6a8ec2b9b8bcee34eb/fence-7` at `5d659f94a` | Merge remote-tracking branch 'origin/develop' into runner/…/AGT-3895 |
| AGT-3897 | `refs/heads/agent-studio/results/run_28bd7ce57a534b648d76f7a9a4cc0710/fence-15` at `60cbb96ad` | AGT-3897: include latest develop |
| AGT-3904 | `refs/heads/agent-studio/results/run_7755d4dd8c8e43f8b776a431589d8365/fence-11` at `509fa2303` | AGT-3904: Merge origin/develop into the secret binding preflight delivery |
| AGT-3909 | `refs/heads/agent-studio/results/run_25de44c7e1e347eabbd54eceb7212adf/fence-3` at `339e6b323` | AGT-3909: Wait for the project runner before the local coding denial start |
| AGT-3911 | `refs/heads/agent-studio/results/run_d6fe533312c84197929d1d04eebe39dc/fence-3` at `1c95d4c25` | AGT-3911: Keep the credential marker when a named value is already masked |
| AGT-3918 | `refs/heads/agent-studio/results/run_6e2136d85b5649869456975b9bdfdef7/fence-5` at `f4bc8bff6` | AGT-3918: Merge origin/develop into the delivery |
| AGT-3919 | `refs/heads/agent-studio/results/run_61d6b7d6bbec42a1a3a85522dd7c34dc/fence-11` at `202637b72` | AGT-3919: Merge develop into the rollback delivery |
| AGT-3921 | `refs/heads/agent-studio/results/run_7d1a2587f2be4fc98d63c8fdaa038dd3/fence-9` at `618de9c8b` | AGT-3921: Merge the M5 delivery onto current develop a fifth time |
| AGT-3923 | `refs/heads/agent-studio/results/run_e2a79054aca44ade92c18d06cc149bc6/fence-7` at `60abf06bf` | AGT-3923: attribute system cards to System, show names and card keys, drop empty tab band |

Of the 57 wave cards, 54 have a report on the runner host. The reports of the
other wave cards name no delivery ref. The conclusion in the Dossier is
therefore that the wave ran, at least in part, through the remote claim in
`LeaseEndpoints`, which applies no scope rule. It is not a claim that all 57
cards ran remotely.
