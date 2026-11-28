export interface DeploymentUrl {
  projectId: string;
  id: string;
  name: string;
  url: string;
}

export interface UrlObservation {
  kind: string;
  statusCode: number | null;
  commit: string | null;
}

export interface InstalledComponent {
  id: string;
  name: string;
  machine: string;
  version: string | null;
  commit: string | null;
  health: string;
  seenAtUtc: string | null;
}

export interface WaitingDelivery {
  key: string;
  title: string;
  area: string;
  flags: string[] | null;
  integratedAtUtc: string | null;
}

export interface DeployHistoryEntry {
  id: string;
  atUtc: string;
  startedAtUtc: string | null;
  endedAtUtc: string | null;
  fromCommit: string | null;
  toCommit: string;
  outcome: string;
  cardCount: number | null;
  steps: { name: string; startedAtUtc: string | null; endedAtUtc: string | null; outcome: string }[];
}

export interface InstallationOverview {
  version: string | null;
  commit: string | null;
  sinceUtc: string | null;
  timeBasis: string | null;
  components: InstalledComponent[];
  branch: string | null;
  tip: string | null;
  verification: { state: string; source: string; atUtc: string | null };
  waiting: WaitingDelivery[];
  detail: string | null;
  history: DeployHistoryEntry[];
}
