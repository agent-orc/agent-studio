/** Format a measured duration in minutes for operator surfaces. */
export function formatDurationMinutes(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return 'Unknown';
  const minutes = Math.max(0, Math.round(value));
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const remainder = minutes % 60;
  if (days) return [`${days} d`, hours && `${hours} h`, remainder && `${remainder} min`].filter(Boolean).join(' ');
  if (hours) return [`${hours} h`, remainder && `${remainder} min`].filter(Boolean).join(' ');
  return `${remainder} min`;
}
