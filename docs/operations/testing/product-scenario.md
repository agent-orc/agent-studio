# Product scenario

`scripts/release/product-scenario.mjs` plays the product scenario of the
[application-lifecycle Dossier](../application-lifecycle/index.html) (card
AGT-3183) against a real installation. Three simulated non-developers evolve
the live website "Click Counter" through Studio: a product manager, a
designer, and a manager. The script runs after the application lifecycle
scenario (AGT-3178), which takes Click Counter to stage and production. It
uses the cards Preview per card (AGT-3180), Product chat with preview
(AGT-3181), and Product role (AGT-3182). The script drives Studio through its
documented API and through a headless Chromium with one browser context per
person (Playwright from `frontend/node_modules`). It writes one report per
execution.

## Run it

```sh
node scripts/release/lifecycle-scenario.mjs ... --teardown never   # once, leaves Click Counter live
node scripts/release/product-scenario.mjs \
  --studio-url https://studio.v1.internal \
  --user-file ~/.config/agent-studio/lifecycle-user.json
```

`--help` lists every option. The report goes to `--report-dir`, else to
`JOB_RESULTS_DIR`, else to `artifacts/product-scenario`. Exit code 0 means the
playthrough passed, 1 that a step failed or was skipped, and 2 a usage error.
`--teardown never` keeps the invited people for inspection; `--teardown only`
cleans up what an earlier run left.

The sign-in file is the Owner's, in the format the lifecycle scenario uses:
`{"login": "...", "password": "...", "totpSecret"?: "<base32>"}` with mode
600. There is no password option and no environment variable, and a
token-shaped value in the environment or on the command line is refused. The
invited people need no files. Each one receives a one-time sign-in link and a
password that the run generates. The password stays in memory and is replaced
in the report.

## People

| Person | Login | Role | Production approval |
|---|---|---|---|
| Product manager | `<prefix>-pm-<run>` | Product (`contributor`) | no |
| Designer | `<prefix>-designer-<run>` | Product | no |
| Manager | `<prefix>-manager-<run>` | Product | yes, granted by the Owner for this project |

The Product role is the IAM role id `contributor`, displayed as "Product". The
card's "contributor" in step 6 is the designer, a Product person. The other
two make the same refused requests through the API.

## Steps

| Step | Person | What the script does | Passes when |
|---|---|---|---|
| Setup: Owner | Owner | `POST /api/auth/login` with the sign-in file | The user is the Owner |
| Setup: project | Owner | Finds Click Counter and its stage and production. Adds a Project URL "Stage (product scenario)" at the stage address unless one exists | Production runs healthy |
| Setup: leftovers | Owner | Disables enabled `<prefix>-*` users, removes their bindings, archives an open card with the wish title | |
| 1 | Owner | `POST /api/auth/invitations` (role `contributor`, the project) for each person. Each person opens the `/?invite=` link and chooses a password ("Join project"). Then `PUT /api/auth/users/{id}` with `productionApprovalProjects` for the manager | `GET /api/auth/users` shows the role, the project, and the approval only on the manager |
| 2 | Product manager | Opens the Project URL with the product controls and sends the wish to the product chat (`observedSurface: "URL preview"`). Answers both questions (where, example) and reviews the proposal. Confirms with "Create this change" | Two questions were asked, the proposal shows a token estimate, no card existed before the confirmation, and then the card exists |
| 3 | Product manager | Waits for Human Review and for the card preview (`TaskInfo.preview`) to be `ready`. Reads the before and after screenshots and opens the preview | The preview answers 200 and the product controls show before and after |
| 4 | Designer | Measures the reset button on the preview at the screenshot's size. Drags the same area on the screenshot, writes the feedback, enters the button's selector, and sends. Reads the card `prompt.md` and waits for a new green delivery | The fix-round prompt holds the comment, selector, crop and address. The preview has a new delivery. The reset button is smaller |
| 5a | Product manager | "Accept this preview" | The card is in Completed |
| 5b | Manager | Waits for the release with the card on stage. The product manager tries to approve production, then the manager approves (`POST .../environments/promotions/approve`) | The product manager gets 403. Production runs the stage digest, healthy |
| 6 | Designer | Credential inventory, secrets, execution hosts, and project settings, for all three people; opens the credentials and environment settings pages | Every request answers 403 |
| Teardown | Owner | Archives an unfinished scenario card and waits for its preview to go. Disables the people, removes their bindings and the scenario's Project URL | No scenario person can sign in |

## Report

`product.json` and `product.html` hold, per step: person, phase, start and end
(UTC), duration, model tokens and core-hours used during the step (the delta
of the workspace quota meter), screenshots of chat and preview, pass, fail or
skip, the reason, and the evidence. Two sections stand on their own. *Feedback
that reached the fix round* holds the comment, the selector, the address, the
crop path, the marked area, the next-round prompt as Studio stored it, and the
reset button before and after. *Refused actions* lists the person, the
request, the status, and the error code. A refusal the scenario did not expect,
such as refused preview feedback, is listed there too. Screenshots are in
`screenshots/`, taken at 1440 x 900.

## Repeating a run

Studio has no route that deletes a user, so each run invites new logins with
the run id. Setup and teardown disable earlier `<prefix>-*` users and remove
their bindings. The teardown step "Delete the invited people" is recorded as
skipped with the logins. Disabled users accumulate until a card adds deletion.
The accepted card stays on the board as the product's history. Its preview is
removed after integration.

## Known gaps (from reading the code, 9 November 2026)

No live run has been recorded yet. Reading the code predicts these failures,
each recorded with its evidence by the step named below:

- Step 4: the Product allowlist (`ContributorAccessPolicy.Allows`) admits
  `POST /api/tasks/{id}/preview-comments`, but not
  `/preview-feedback`. The route the product controls call answers
  `403 role-denied` for a Product session. The designer's feedback therefore
  does not reach a fix round.
- Step 3: the shipped deploy host starts `DeployHostCommand` without an
  `IPreviewRunZone` (`runner/Program.cs`). A preview job fails with "Preview run
  zone is not configured" until AGT-3229 or AGT-3233 supplies the run zone.
- Steps 2 to 5: `GET /api/projects/{id}/urls/**` (readiness, context and the
  preview proxy of the Project URL tab) and `/api/projects/{p}/previews/**`
  are not on the Product read allowlist. A Product session may see the tab
  without its live frame, and the card preview image may not load.
- The feedback form sends the Project URL address (stage) as the preview
  address, not the card's delivery address
  (`project-url-preview-tab` passes `displayUrl()`).

## Tests

`node --test scripts/release/product-scenario/test/*.test.mjs` plays the
whole scenario against an in-process fake Studio with a fake clock and a fake
browser that acts like the product controls. It covers the happy path, the
refused preview feedback, a preview that never becomes ready, a fix round
that ignores the feedback, a reachable credential route, a project that is
not live, a non-Owner, a rerun, teardown only, the configuration guards, and
the report. `npm --prefix frontend run lint` runs it as
`lint:product-scenario`.
