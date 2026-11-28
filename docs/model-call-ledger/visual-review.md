# Prototype review

The three views use Studio's shared semantic tokens, Inter and the existing workspace navigation vocabulary. The Explorer starts with the selected window and a stacked hourly series. Grouping and comparison reveal more detail without hiding the counts. The host view pairs source and unit with each stage. The card view follows the lifecycle and puts a single response beside the measured coding subtotal. Phone layouts stack the evidence below each scope.

| Rubric | Initial round | Final round | Evidence |
| --- | ---: | ---: | --- |
| Content | 3 | 4 | Explorer: comparison and visible token-cost composition. Host pipeline: named hosts and counts by source unit. Card: cost split, explicit gaps and response provenance. |
| Originality | 3 | 4 | The same night links to both host placement and a single response. |
| Design | 3 | 4 | The chart, ranked grid and response inspector use separate widths on desktop and stack at 390 CSS pixels. |
| Feel | 3 | 4 | Stable view links, native controls and no decorative motion. |
| Technical execution | 4 | 4 | Chromium verification at 1440 and 390 CSS pixels in both themes. Tested view switching, grouping, comparison, scenario selection, image loading, theme toggle and viewport overflow. |

The first review showed dense chart labels on phones, a duplicated breadcrumb and a lifecycle table without its own cost breakdown. The revision uses larger mobile chart labels, a shorter breadcrumb, explicit cost components, and separated host metrics. It carries forward the token-class composition from AGT-4393.

Repository captures are in [assets](assets/). The collected task results contain 12 final desktop and phone captures, in light and dark, across the three views. The capture uses the standalone prototype and waits for the visible view and loaded fonts. It checks console and page errors. The existing product figures use an earlier pinned product capture and identify that provenance separately.

The prototype uses measured coding data and historical reconstructions. Its complete comparison example covers selected cohorts. Production controls for arbitrary rotations, live coverage, and full card history are specified in the Dossier.
