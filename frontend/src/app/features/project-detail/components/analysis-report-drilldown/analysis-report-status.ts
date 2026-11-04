import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/**
 * State-to-tone mappings for analysis reports (AGT-3403). Shared by the
 * report list (project-analysis-reports-section) and the drill-down.
 * Unknown values fall back to `neutral`.
 */
const REPORT_SEVERITY_TONES: Readonly<Record<string, StatusTone>> = {
  Info: 'neutral',
  Warn: 'warning',
  High: 'warning',
  Critical: 'danger',
};

/** Report severity: Info neutral, Warn and High warning, Critical danger. */
export function reportSeverityTone(severity: string | null | undefined): StatusTone {
  return (severity && REPORT_SEVERITY_TONES[severity]) || 'neutral';
}

const FINDING_SEVERITY_TONES: Readonly<Record<string, StatusTone>> = {
  Info: 'neutral',
  Warn: 'warning',
  High: 'danger',
};

/** Finding severity: Info neutral, Warn warning, High danger. */
export function findingSeverityTone(severity: string | null | undefined): StatusTone {
  return (severity && FINDING_SEVERITY_TONES[severity]) || 'neutral';
}

const PARSE_STATUS_TONES: Readonly<Record<string, StatusTone>> = {
  Structured: 'success',
  Unstructured: 'warning',
  MalformedJson: 'danger',
};

/** Sidecar parse status: Structured success, Unstructured warning, MalformedJson danger. */
export function parseStatusTone(status: string | null | undefined): StatusTone {
  return (status && PARSE_STATUS_TONES[status]) || 'neutral';
}
