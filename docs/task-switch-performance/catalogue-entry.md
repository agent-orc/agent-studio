# Catalogue entry

The canonical Dossier is `docs/task-switch-performance/index.html`, sourced
from AGT-2910. Its folder follows the later, explicit concept-mode contract
`docs/<slug>/`, rather than the earlier suggested operations path.

This run may change exactly one Dossier directory. The following changes to
existing navigation are prepared for implementation task 7; **they are not
applied**. No North star registration or human sight review is claimed.

In `docs/operations/nordstern/index.html`, add to the current-Dossier map:

```html
<a class="card" href="../../task-switch-performance/index.html">
  <h4>Task switch performance</h4>
  <div class="path">task-switch-performance/ · Source AGT-2910</div>
  <p>A bounded task core within 100 ms, progressive detail resources,
    and versioned Git facts outside request paths. Decision pending.</p>
</a>
```

In `docs/start/README.md`, add this row in the load-bearing entry points:

```markdown
| Task switch performance: measured baseline, bounded core, progressive detail and Git snapshots (AGT-2910) | [decision Dossier](../task-switch-performance/index.html) |
```

The North star remains [the standing map](../operations/nordstern/index.html#s5).
Do not invent an `AGT-W` key. Add a server-assigned key when cataloguing has
actually assigned one. Do not duplicate or relocate this canonical directory.
