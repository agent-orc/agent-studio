export interface PersonaEntry {
  id: string; title: string; aspect: string; kind: string; revision: string;
  adoptableRevision: string | null;
  descriptor: { charter: string; cadence: { report: string }; budget: { tokensPerWeek: number } };
  activation: null | { personaId: string; roleId?: string; state: string; descriptorRevision: string;
    allowance: { tokensPerWeek: number }; history: { at: string; action: string; state: string; descriptorRevision: string }[] };
}
export interface PersonaItem { id: string; personaId: string; roleId: string; kind: string; title: string;
  body: string; importance: number; state: string; createdAtUtc: string; deadlineUtc?: string | null;
  impact: number; urgency: number; confidence: number; floor: number; draftCard?: boolean;
  dossierKey?: string | null; dossierSection?: string | null; draftTitle?: string | null; draftPrompt?: string | null;
  deferredUntilUtc?: string | null; promotedTaskKey?: string | null }
export interface PersonaReport { id: string; personaId: string; publishedAtUtc: string; state: string;
  template: string; reason: string; openItems: { itemId: string; title: string; importance: number }[];
  sourcesRead: string[]; sourcesMissing: string[]; tokensUsed: number }
export interface PersonaBudget { poolTokens: number; usedTokens: number; startUtc: string; endUtc: string;
  personas: Record<string, { allowanceTokens: number; usedTokens: number }> }
export interface PersonaCard { entry: PersonaEntry; used: number; allowance: number; report: PersonaReport | null;
  highest: number | null; overdue: boolean }
/** Open, draft and deferred items still take an owner decision; a deferred proposal stays decidable. */
export const isOpenPersonaItem = (item: PersonaItem): boolean =>
  item.state === 'open' || item.state === 'draft' || item.state === 'deferred';
export function personaCards(entries: PersonaEntry[], budget: PersonaBudget | null,
  items: PersonaItem[], reports: PersonaReport[], now = new Date()): PersonaCard[] {
  return entries.filter(entry => !!entry.activation).map(entry => {
    const active = entry.activation!;
    const report = reports.filter(row => row.personaId === entry.id)
      .sort((a, b) => b.publishedAtUtc.localeCompare(a.publishedAtUtc))[0] ?? null;
    const highest = items.filter(row => row.personaId === entry.id && isOpenPersonaItem(row))
      .reduce<number | null>((max, row) => Math.max(max ?? 0, row.importance), null);
    const allowance = budget?.personas[entry.id]?.allowanceTokens ?? active.allowance.tokensPerWeek;
    const used = budget?.personas[entry.id]?.usedTokens ?? 0;
    const period = Number(entry.descriptor.cadence.report.match(/^P(\d+)D$/)?.[1] ?? 7);
    const overdue = active.state !== 'inactive' && active.state !== 'paused'
      && (!report || now.getTime() - Date.parse(report.publishedAtUtc) > period * 86_400_000);
    return { entry, report, highest, allowance, used, overdue };
  });
}
export function poolTotals(cards: PersonaCard[], pool: number) {
  const allocated = cards.reduce((sum, card) => sum + card.allowance, 0);
  const used = cards.reduce((sum, card) => sum + card.used, 0);
  return { used, allocated, unallocated: Math.max(0, pool - allocated), pool };
}
