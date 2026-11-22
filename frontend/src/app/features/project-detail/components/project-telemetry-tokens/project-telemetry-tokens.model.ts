export interface TelemetryToken {
  id: string; name: string; project: string; environment: string; signals: string[];
  createdAt: string; createdBy: string | null; expiresAt: string | null; lastUsedAt: string | null; revokedAt: string | null;
}
export interface TelemetryTokenCreated {
  token: TelemetryToken; secret: string; otlpEndpoint: string; otlpHeaders: string;
}

/** createdBy of the token Studio mints for deploys of an environment with `telemetry: true`. */
export const DEPLOY_TOKEN_CREATOR = 'studio:deploy';

export type TelemetryTokenState = 'active' | 'revoked' | 'expired';

export function tokenState(token: TelemetryToken, now: Date): TelemetryTokenState {
  if (token.revokedAt) return 'revoked';
  return token.expiresAt && new Date(token.expiresAt).getTime() <= now.getTime() ? 'expired' : 'active';
}

/** The environment lines an SDK or collector needs; the headers line carries the plaintext token. */
export function exporterSettings(created: TelemetryTokenCreated): string {
  return [
    `OTEL_EXPORTER_OTLP_ENDPOINT=${created.otlpEndpoint}`,
    'OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf',
    `OTEL_EXPORTER_OTLP_HEADERS=${created.otlpHeaders}`,
  ].join('\n');
}
