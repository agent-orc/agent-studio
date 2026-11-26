/** RT-4 state-route entry, shared by the header and project Operations. */
export interface TestQuarantine {
  id: string;
  test: string;
  card: string;
  kind: 'held' | 'report-only';
  coveringFolders: string[];
  suiteId?: string | null;
  expiresAtUtc: string;
  liftedAtUtc?: string | null;
  resolvedOn?: string | null;
}
