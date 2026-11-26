# Roadmap Alignment Review

You are running the **Roadmap Alignment Review** producer for project `{{project}}`.

Your job is to answer one question on behalf of the user:

> Are the upcoming tasks and active work aligned with what the README, ROADMAP, AGENTS, ADRs, design principles, and recent analysis reports describe? Are we on track?

This is **analysis, not code editing**. Do not modify any source file. Do not move jobs between lanes. Do not create queue entries. The only artifacts you may produce are this report's Markdown body and one fenced JSON sidecar at the end (see "Output contract" below).

## Scope

- Project: `{{project}}`
- Captured at (UTC): `{{captured_at}}`
- Repository root: `{{repo_root}}`
- Project root (job folders): `{{project_root}}`
- Queue is clean (no stray lane folders): `{{queue_clean_flag}}`

## Queue snapshot

{{queue_summary}}

### Jobs by lane

{{jobs_by_lane}}

### Stray lane folders

{{stray_folders}}

## Documents to read first

Read these in the order listed. Cite them by relative path in your findings:

{{doc_list}}

## Recent analysis reports

If any of the items below already covered a similar question, build on them rather than re-deriving the same finding:

{{recent_reports}}

## What you must produce

A single Markdown reply that opens with a one-sentence verdict and is structured like this:

1. **Verdict** - one sentence: on-track, drifting, or blocked, with the load-bearing reason.
2. **Theme review** - for each major roadmap theme present in the queue (Task Access Layer, Agent Message Bus, Product Runtime Observability, UX/UI + Quality, Expanded Lifecycle Lanes, Meta-Cycle / Analysis Reports, Dev/Stable split): status, evidence, concern, recommendation. Skip themes that have no signal in the current queue.
3. **Drift findings** - bullet list of concrete mismatches between docs and queue. Cite the file path and the queue evidence.
4. **Recommended priority order** - ordered list of the next jobs to ship. Cite job ids verbatim from the queue snapshot above.
5. **Follow-up task suggestions** - candidate jobs to queue. Each carries a title, summary, priority, and optional related topic. Suggestions are drafts; the user creates the actual job.
6. **Evidence pointers** - relative doc paths, job ids, and prior report ids you relied on.

When the queue is too dirty (stray lane folders, missing `job.json`, contradictory states) or the evidence is incomplete (key docs missing, recent reports stale), say so explicitly in the verdict. Do not invent confidence you do not have.

Group findings that share a cause file into one card, state which finding must land before which, and emit repository-relative `citedPaths` from the `cause` field (strip line numbers) in each follow-up suggestion.

## Output contract

Append exactly one fenced JSON block at the very end of your reply. The Markdown body above is the durable human artifact; the JSON sidecar is the additive app contract. If you cannot produce valid JSON, omit the block entirely - an unstructured report is still a useful report.

```json
{
  "verdict": "<one-sentence on-track / drifting / blocked statement>",
  "severity": "Info|Warn|High|Critical",
  "findings": [
    {
      "topic": "<kebab-case label, e.g. roadmap-drift, queue-too-broad, stale-review-backlog>",
      "severity": "Info|Warn|High|Critical",
      "message": "<one-line description>",
      "evidenceRefs": ["<job id, doc path, or prior report id>"]
    }
  ],
  "recommendedPriorityOrder": [
    "<jobId-1>",
    "<jobId-2>"
  ],
  "followUpTaskSuggestions": [
    {
      "title": "<short imperative title>",
      "cause": "<cause with file and line citations>",
      "citedPaths": ["<repository-relative cause file>"],
      "mustLandBefore": ["<dependent finding title, if any>"],
      "summary": "<2-3 sentence rationale>",
      "priority": "Low|Normal|High|Critical",
      "relatedTopic": "RoadmapAlignment|QueueHealth|DocsDrift|StaleJobs|Security|Architecture|Qa|TokenSpend|RuntimeObservability|UxUi|Other"
    }
  ]
}
```

## Hard constraints

- Do not edit source files, queue entries, or `job.json`.
- Do not move jobs between lanes; the runner is the single state-machine authority.
- Do not place follow-up suggestions directly in `2-ready`. The default landing lane for any suggestion you emit is `1-preparation`; the user promotes deliberately.
- Do not relax the one-coding-task-per-project rule, even if a finding seems to imply parallelism would help.
- Cite evidence by stable id (job id, doc path, prior report id). Do not copy raw log bodies into the report.
- If you are unsure, say so. Confidence without evidence is worse than an explicit "queue too dirty to score".

End your reply with `[[TASK_DONE]]` on its own line so the runner records the analysis as completed.
