/**
 * Lane state -> concept-doc topic for the lane-info modal.
 *
 * Each topic matches a committed file at <c>docs/app/help/lane-guides/{topic}.md</c>
 * served by <c>GET /api/concept-docs/{topic}</c>. Virtual sub-lanes
 * (e.g. <c>2-ready-intake</c>, <c>4-review</c>) collapse to their parent
 * lane's doc so the lane-info trigger reads the same prose everywhere a
 * lane is shown — board headers and the studio-shell active panel alike.
 *
 * AGT-2715: the topic table moved into the lane presentation catalogue
 * (`models/lane-presentation.ts`) so a lane's help doc, name, tone, and glyph
 * are declared together and cannot drift apart. This file stays as the
 * long-standing import point for the info-button call sites.
 */
export { laneDocTopic } from '../../models/lane-presentation';
