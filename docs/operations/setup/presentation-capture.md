# MVP presentation capture runbook

This runbook produces the 16:9 product media used by the MVP slide deck and
landing page. Capture uses only the deterministic ADR-0056 pinned workspace.
Never point the capture process at a production workspace.

The source of truth for the ordered outputs is the
[MVP presentation storyboard](../../concepts/mvp-presentation-storyboard.md).

## Safety gate and demo reset

Before any capture, confirm all of the following:

- The visible project picker contains only `Demo App` and `Demo Platform`.
- The cards use `DEMO-*` or `PLAT-*` keys. Stop immediately if any real project
  or task name appears.
- Browser notifications, email, chat overlays, password managers, and desktop
  notifications are closed or suppressed.
- The output folder contains no earlier production capture.

Reset the default pinned demo store from the repository root:

```sh
node scripts/seed-demo-workspace.mjs
```

The seed refuses the production workspace root. After reset, open the demo
backend configured for `C:\Projects\agent-taskboard-workspace-demo`, select
`Demo App`, and verify the visible keys again. Do not point ScreenToGif or OBS
at the stable or production browser window.

Each demo `WatchPaths` entry needs a `RootPath` (or `RepositoryPath`) pointing
at its project folder, not only a `Path`. The seeded Wiki tree and the Dossier
gallery live in `<RootPath>/docs`, and without that entry no repository root
resolves and both surfaces stay empty.

## The two pinned inputs

The seed reads two committed files and invents nothing at generation time:

| Input | Owns | Origin |
|---|---|---|
| [pinned-seed.json](../../../scripts/presentation-capture/pinned-seed.json) | Projects, the task list per lane, and the escalated decision card. | Sanitizing export of a real board (see below). |
| [pinned-demo-content.json](../../../scripts/presentation-capture/pinned-demo-content.json) | Both Wiki trees, the six-state Dossier gallery, and the card-to-document edges. | Authored. Knowledge content of a publicly browsable instance is never exported from a workspace. |

Both are rendered by one generator, so a page and a Dossier stay in the same
voice and a re-seed stays byte-identical. Acceptance coverage for the seed
(byte-identical generation, all lanes, all Dossier lifecycle states, and the
data boundary of the visitor surface) runs with:

```sh
node --test scripts/seed-demo-workspace.test.mjs
```

## Updating the pinned snapshot from real data

Pinned is the source rule for documentation and marketing captures. A real
board and task may be used only during an explicit snapshot update: the export
rewrites project names, paths, repository references, and task keys to the
`Demo App` / `Demo Platform` and `DEMO-*` / `PLAT-*` vocabulary, fixes the time
base, and writes a versioned JSON snapshot. Review the resulting diff for
private data before committing it. The normal capture command never reads the
source workspace.

```sh
node scripts/presentation-capture/export-pinned-seed.mjs \
  --source-root <workspace-root> \
  --project <source-project-key> \
  --task <source-task-key> \
  --secondary-project <optional-source-project-key>
```

## Deterministic stills with Playwright

From the repository root, one command resets a temporary demo store, starts an
isolated dynamic-port backend and frontend, captures every still in both
themes, and tears the stack down:

```sh
npm --prefix frontend run docs:presentation
```

The command uses marketing mode, a 1920x1080 CSS viewport, and a device scale
factor of 2. Each presentation PNG is therefore 3840x2160 and scales cleanly into a
1920x1080 slide. Outputs are written to
`docs/assets/images/presentation/`. Do not resize the source files before
placing them in the deck. Fit them proportionally inside the slide frame.

The filenames include their order, theme, and `--pinned` source claim. A failed
dimension check fails the Playwright run instead of leaving a soft screenshot
in the output set.

The operator website page adds seven paired scenes, numbered 09 through 15.
Their authored scenario copy lives in `pinned-seed.json` under
`operatorScenes`. The Playwright capture opens the isolated pinned installation
and places clearly labeled scenario panels beside its demo board, card and chat.
These panels illustrate operator records without making a token or a live host
part of the public demo. The page source is
[Operate Studio from your coding agent](../../website/operator-agent/index.html).
The website owner (MKT/AOW) integrates and publishes the page.

The operator capture is also runnable by itself after the demo stack is up:

```sh
cd frontend
PW_VISUAL_CAPTURE=marketing npx playwright test \
  e2e/visual-evidence/operator-page-capture.spec.ts --project=visual-evidence
```

For the delivery proof, run the focused capture twice from a fresh demo reset,
save the ordered SHA-256 lists under `$JOB_RESULTS_DIR`, and compare them.

The two landing-page pairs render at most three Playwright-owned annotation
labels. They are enabled by default and are part of the browser DOM at capture
time, not a later image edit. Disable them for a clean product-only variant:

```sh
PW_PRESENTATION_ANNOTATIONS=0 npm --prefix frontend run docs:presentation
```

For a reproducibility check, run the command twice and compare the ordered PNG
hash list. In a managed task, store both lists and the comparison under
`$JOB_RESULTS_DIR`.

## Silent loops with ScreenToGif

Use ScreenToGif only for storyboard rows marked as a silent loop.

1. Reset and verify the demo workspace using the safety gate above.
2. Set the browser window to 1920x1080 and 100 percent browser zoom. Keep the
   application full frame visible. Record one theme per loop as named in the
   storyboard.
3. In ScreenToGif Recorder, select a 1920x1080 capture region and 15 fps. Turn
   audio off. Keep the webcam absent.
4. Enable cursor capture. Use a restrained high-contrast cursor halo and click
   ring. Keep the pointer still when the viewer should read.
5. Rehearse the path once, reset the starting surface, then record 6 to 10
   seconds. Pause for about one second at the first and last states so the loop
   is readable.
6. In the editor, remove setup frames, accidental hover noise, and dead time.
   Keep the interaction at normal speed. Add no narration or baked-in caption.
7. Export GIF for maximum slide compatibility. If the deck supports embedded
   MP4 loops, also export H.264 at 1920x1080 and 15 fps using the same basename.

Use the exact storyboard filename, including theme and `--pinned`. Review the
loop at slide size and confirm it restarts without a distracting jump.

## Narrated backup with OBS Studio

The narrated backup is optional and contains microphone audio but no webcam.

1. Create an OBS scene named `MVP Demo Only` with one Window Capture source for
   the verified demo browser. Do not use Display Capture when Window Capture is
   available.
2. Add one microphone input. Remove or disable every Video Capture Device and
   webcam source. Disable desktop audio to avoid notifications or meeting audio.
3. Set Base Canvas and Output Resolution to 1920x1080, Common FPS to 30, and
   output to MKV using the high-quality recording preset. MKV protects the take
   if recording is interrupted.
4. Set the browser to 1920x1080 at 100 percent zoom. Use the dark theme named in
   the storyboard. Keep the cursor visible with the operating-system cursor
   emphasis enabled, but do not add a large novelty cursor.
5. Reset the demo, rehearse the storyboard from start to finish, then record a
   60 to 90 second take. Leave two seconds of silence at both ends.
6. Check microphone level before the take. Peaks should remain below 0 dB and
   normal speech should sit roughly between -18 dB and -6 dB.
7. Use OBS `File > Remux Recordings` to create the final MP4. Name it exactly as
   listed in the storyboard. Confirm that it has microphone audio, no desktop
   audio, no webcam, and no production data before sharing it.

## Final quality check

- Every still exists in both themes and is exactly 3840x2160.
- Silent loops contain no audio or webcam and use the requested theme.
- The narrated backup is 1920x1080 at 30 fps, microphone only, with no webcam.
- Captions live in the deck, not baked into product media.
- Every filename matches the storyboard and contains `--pinned`.
- Two consecutive capture runs produce identical SHA-256 values for all four
  landing-page PNGs in storyboard rows 07 and 08.
- A second person checks the first and last frame of every motion asset for
  production data before it leaves the operator machine.
