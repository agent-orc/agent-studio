# Ready

The `2-ready` lane is the queue of refined, runnable tasks waiting for a coding agent. A card here has everything an agent needs: a clear prompt, scope, and acceptance criteria. Runners pull their work from this lane.

## How pickup works

Each project runs its own Ready queue from the top: the card marked **Next** runs when that project has a free slot. By default a project has one slot, so its next card starts once its current run leaves `3-progress`. A project with parallel slots (`maxParallelism`) starts its next cards as slots free up, and different projects run side by side.

The queue order is the lane's sort setting for that project. The default, Lane entry, puts cards placed by hand (Move to top or a drag) first in the order you set, then the remaining cards with the most recently entered on top. The lane header tooltip names the setting in use. The runner skips a card it cannot start yet, for example one that waits on another task, and the Next marker moves to the card it will take instead.

On the All-projects board the lane shows each project's queue in turn, grouped by project, so there is no single order across projects. Filter to one project to see its queue on its own.

The lane also splits into sub-groups: tasks a human marked ready, and tasks the orchestrator's intake has cleared. A card you marked ready still passes an intake check before a runner claims it. To change what runs next, use Move to top on a card or drag it into place. Only that card moves; the other cards keep their order.
