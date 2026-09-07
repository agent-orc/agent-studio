/**
 * Lane state -> concept-doc topic for the lane-info modal.
 *
 * The map itself moved into {@link LANE_PRESENTATION}
 * (`src/app/models/lane-presentation.ts`) with the rest of a lane's
 * presentation, so a lane's help topic, name, tone, and glyph are declared
 * side by side and cannot drift apart. This file stays as the import path the
 * board lane header and the task-status-card already use.
 */
export { laneDocTopic } from '../../models/lane-presentation';
