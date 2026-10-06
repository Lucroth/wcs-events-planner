/** Written by the sync job (C#), read-only here. */
export interface ScrapedEvent {
  id: string;
  name: string;
  dateFrom: string;
  dateTo: string;
  year: number;
  city: string | null;
  country: string | null;
  isWsdc: boolean;
  ticketUrl: string | null;
  coords: { lat: number; lng: number } | null;
  airports: { iata: string; name: string }[] | null;
  station: { slug: string; name: string } | null;
  strengths: Strength[];
  strengthsFrom: { id: string; name: string; dateFrom: string | null } | null;
  results: DivisionResult[];
  /** Added by hand in the admin; never touched by the sync. */
  manual?: boolean;
}

export type Difficulty = "Easy" | "Medium" | "Hard";

export interface Strength {
  division: string;
  role: "Leader" | "Follower";
  fieldSize: number;
  averagePoints: number;
  medianPoints: number;
  /** Mean of the strongest quarter of the field; what the difficulty is ranked by. Absent on documents published before it existed. */
  topQuartileAverage?: number;
  difficulty: Difficulty | null;
}

export interface DivisionResult {
  division: string;
  roundName: string;
  roundId: number;
  places: { position: number; names: string }[];
}

export interface YearSummary {
  events: {
    id: string;
    name: string;
    dateFrom: string;
    dateTo: string;
    city: string | null;
    country: string | null;
    isWsdc: boolean;
    chips: Chip[];
  }[];
}

/** One division of an event in the list: its level and the top-quartile points it was ranked by. */
export interface Chip {
  division: string;
  level: Difficulty | null;
  top?: number;
}

export type PassKind = "Full" | "Party";

export interface Pass {
  kind: PassKind;
  tier: string;
  price: number;
  currency: string;
  /** Last day on sale, yyyy-MM-dd; null for the tier sold until the door. */
  until: string | null;
}

export interface Override {
  name?: string;
  dateFrom?: string;
  dateTo?: string;
  city?: string;
  country?: string;
  isWsdc?: boolean;
}

/** Owned by the admin. The sync only reads `override`, the venue and `airports`. */
export interface Info {
  year?: number;
  override?: Override | null;
  websiteUrl?: string | null;
  facebookUrl?: string | null;
  instagramUrl?: string | null;
  streamUrl?: string | null;
  polishGroupUrl?: string | null;
  /** Local time at the event, yyyy-MM-ddTHH:mm. */
  registrationOpens?: string | null;
  venueName?: string | null;
  venueAddress?: string | null;
  lat?: number | null;
  lng?: number | null;
  airports?: string[];
  staff?: string[];
  eventSchedule?: string | null;
  compSchedule?: string | null;
  passes?: Pass[];
}

export interface Leg {
  airline: "Ryanair" | "Wizz";
  from: string;
  to: string;
  date: string;
  times: string[];
  arrival: string | null;
  durationMinutes: number | null;
  price: number;
  currency: string;
}

export interface Flights {
  eventId?: string;
  key?: string;
  /** Cheapest return combination per person. */
  cheapest?: number | null;
  origins: string[];
  destinations: string[];
  currency: string;
  combos: { out: Leg; back: Leg; perPerson: number }[];
  out: Leg[];
  back: Leg[];
  fetchedOn: string;
}

export interface TrainLeg {
  date: string;
  departure: string;
  arrival: string;
  durationMinutes: number;
  changes: number;
  price: number;
}

export interface Trains {
  eventId: string;
  city: string;
  station: string;
  currency: string;
  /** Cheapest out plus cheapest back, per person; null when one direction had no fare on sale. */
  cheapest: number | null;
  out: TrainLeg[];
  back: TrainLeg[];
  fetchedOn: string;
}

/** An event as readers see it: scraped facts with the admin's corrections on top. */
export interface EventView {
  id: string;
  name: string;
  dateFrom: string;
  dateTo: string;
  city: string | null;
  country: string | null;
  isWsdc: boolean;
}

export function applyOverride<T extends EventView>(e: T, info: Info | undefined): T {
  const o = info?.override;
  if (!o) return e;
  return {
    ...e,
    name: o.name || e.name,
    dateFrom: o.dateFrom || e.dateFrom,
    dateTo: o.dateTo || o.dateFrom || e.dateTo,
    city: o.city || e.city,
    country: o.country || e.country,
    isWsdc: o.isWsdc ?? e.isWsdc,
  };
}

/** The tier on sale today: nearest deadline not yet passed, else the open-ended door price. */
export function currentPass(passes: Pass[] | undefined, kind: PassKind, today: string): Pass | null {
  const ofKind = (passes ?? []).filter((p) => p.kind === kind);
  const dated = ofKind.filter((p) => p.until && p.until >= today).sort((a, b) => a.until!.localeCompare(b.until!));
  return dated[0] ?? ofKind.find((p) => !p.until) ?? null;
}

/** Country names as scoring.dance spells them, the same list as Countries.IsEuropean in the sync. */
const europe = new Set(
  [
    "Austria", "Belgium", "Bulgaria", "Switzerland", "Czechia", "Czech Republic", "Germany", "Denmark", "Spain",
    "Estonia", "Finland", "France", "United Kingdom", "Great Britain", "Greece", "Croatia", "Hungary", "Ireland",
    "Iceland", "Italy", "Lithuania", "Latvia", "Netherlands", "The Netherlands", "Norway", "Poland", "Portugal",
    "Romania", "Russia", "Slovakia", "Slovenia", "Sweden", "Turkey", "Ukraine", "Serbia", "Luxembourg", "Malta",
    "Cyprus", "Belarus", "Moldova", "Bosnia and Herzegovina", "Montenegro", "North Macedonia", "Albania",
  ].map((c) => c.toLowerCase()),
);

/** An event with no country is left out: the admin can set one. */
export const isEurope = (country: string | null | undefined): boolean => !!country && europe.has(country.trim().toLowerCase());

/**
 * Whether an event's division chips pass the list filter. With a division picked, only that
 * division's level counts; without one, any division at a chosen level does.
 */
export function matchesLevel(
  chips: Chip[],
  division: string | null,
  levels: Difficulty[],
): boolean {
  const candidates = division ? chips.filter((c) => c.division === division) : chips;
  if (!levels.length) return !division || candidates.length > 0;
  return candidates.some((c) => c.level !== null && levels.includes(c.level));
}

/** WSDC Registry Event Rules, Chart 5: tiers by unique competitors per role, and the points each awards. */
export const tiers = [
  { tier: 1, min: 5, max: 10, points: [3, 2, 1, 0, 0], extra: "" },
  { tier: 2, min: 11, max: 19, points: [6, 4, 3, 2, 1], extra: "" },
  { tier: 3, min: 20, max: 39, points: [10, 8, 6, 4, 2], extra: "1 (up to 10th)" },
  { tier: 4, min: 40, max: 79, points: [15, 12, 10, 8, 6], extra: "1 (up to 12th)" },
  { tier: 5, min: 80, max: 129, points: [20, 16, 14, 12, 10], extra: "2 (up to 15th)" },
  { tier: 6, min: 130, max: Infinity, points: [25, 22, 18, 15, 12], extra: "2 (up to 15th)" },
] as const;

/** The tier a field of this size competes at; null below 5, where no points are awarded. */
export const tierFor = (competitors: number) => tiers.find((t) => competitors >= t.min && competitors <= t.max) ?? null;

const levelRank: Record<Difficulty, number> = { Easy: 0, Medium: 1, Hard: 2 };

/**
 * How hard an event is for sorting: with a division picked, that division's top-quartile points;
 * otherwise the average level across divisions, top-quartile points breaking ties. Null without data.
 */
export function levelScore(chips: Chip[], division: string | null): number | null {
  if (division) {
    const c = chips.find((x) => x.division === division);
    return c?.top ?? (c?.level ? levelRank[c.level] : null);
  }
  const known = chips.filter((c) => c.level);
  if (!known.length) return null;
  const avgLevel = known.reduce((s, c) => s + levelRank[c.level!], 0) / known.length;
  const avgTop = known.reduce((s, c) => s + (c.top ?? 0), 0) / known.length;
  return avgLevel * 1000 + avgTop;
}
