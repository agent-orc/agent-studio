/** The brief versions a content review compares. */
export interface BriefVersions {
  currentBriefVersion: string | null | undefined;
  deliveryBriefVersion: string | null | undefined;
  reviewBriefVersion: string | null | undefined;
}

/**
 * How the delivery's and the verdict's brief versions stand against the
 * current brief. `changed`: a recorded version differs from the current one.
 * `unrecorded`: a version is missing, so nothing shows the brief changed
 * (F12: a missing version was read as a changed brief).
 */
export type BriefGap = 'changed' | 'unrecorded';

function recorded(version: string | null | undefined): version is string {
  return !!version?.trim();
}

export function briefGapOf(review: BriefVersions | null | undefined): BriefGap | null {
  if (!review) return null;
  const current = review.currentBriefVersion;
  const versions = [review.deliveryBriefVersion, review.reviewBriefVersion];
  if (recorded(current) && versions.some(version => recorded(version) && version !== current)) return 'changed';
  if (!recorded(current) || versions.some(version => !recorded(version))) return 'unrecorded';
  return null;
}

/** Which record lacks a brief version, for the owner-facing sentence. */
export function unrecordedBriefSubject(review: BriefVersions): string {
  if (!recorded(review.currentBriefVersion)) return 'the current brief';
  const delivery = !recorded(review.deliveryBriefVersion);
  const verdict = !recorded(review.reviewBriefVersion);
  if (delivery && verdict) return 'this delivery or its content verdict';
  if (verdict) return 'the content verdict of this delivery';
  return 'this delivery';
}

/** The brief gap as one secondary-gap sentence. */
export function briefGapSentence(gap: BriefGap | null, review: BriefVersions | null | undefined): string | null {
  if (!gap || !review) return null;
  return gap === 'changed'
    ? 'The brief changed after this delivery.'
    : `No brief version was recorded for ${unrecordedBriefSubject(review)}.`;
}
