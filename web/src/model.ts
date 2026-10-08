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
  /** Neighbouring editions of the same series; absent on documents published before they existed. */
  previous?: EditionRef | null;
  next?: EditionRef | null;
  results: DivisionResult[];
  /** Added by hand in the admin; never touched by the sync. */
  manual?: boolean;
  /** An edition the organiser announced before scoring.dance lists it; replaced once it does. */
  announced?: { venue: string | null; source: string | null } | null;
  /** The venue's street address from the WSDC calendar, when it lists the event. */
  venueAddress?: string | null;
}

export interface EditionRef {
  id: string;
  name: string;
  dateFrom: string | null;
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
  /** The same figure averaged over every European event of this division and role. */
  europeTopQuartileAverage?: number;
  difficulty: Difficulty | null;
}

export interface DivisionResult {
  division: string;
  roundName: string;
  roundId: number;
  /** `dancers` is absent on documents published before it existed; `wscid` only for dancers with WSDC points. */
  places: { position: number; names: string; dancers?: { name: string; wscid: number | null }[] }[];
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
    /** Not listed yet: the series' latest edition (whose id this is) moved a year on. */
    expected?: boolean | null;
    /** Dates the organiser has announced for an expected edition, before scoring.dance lists it. */
    announced?: { venue: string | null; websiteUrl: string | null; source: string | null } | null;
  }[];
}

/** One division of an event in the list: its level and the top-quartile points it was ranked by. */
export interface Chip {
  division: string;
  level: Difficulty | null;
  top?: number;
  /** One role's own figures; absent on summaries published before they existed. */
  leader?: Side | null;
  follower?: Side | null;
}

export interface Side {
  level: Difficulty | null;
  top: number;
}

export type DanceRole = "leader" | "follower";

/** A chip as seen by a reader dancing one role: that role's level and points, else both roles averaged. */
export const sideOf = (c: Chip, role: DanceRole | null): Side => (role && c[role]) || { level: c.level, top: c.top ?? 0 };

export type PassKind = "Full" | "Party";

export interface Pass {
  kind: PassKind;
  tier: string;
  price: number;
  currency: string;
  /** Last day on sale, yyyy-MM-dd; null for the tier sold until the door. */
  until: string | null;
  /** The tier on sale now, as the ticket seller says; set by the DanceApp scraper, which the dates cannot always tell. */
  current?: boolean;
  /** No tickets left: the seller offers a waiting list. */
  soldOut?: boolean;
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
  /** A pasted image of the schedule exists in schedules/{id}_event or _comp; kept apart so the list never downloads it. */
  eventScheduleImage?: boolean;
  compScheduleImage?: boolean;
  passes?: Pass[];
  /** Set when the sync filled fields from the event's website; dropped when the admin saves. */
  autofill?: { source: string; on: string; fields: string[] } | null;
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
  return ofKind.find((p) => p.current) ?? dated[0] ?? ofKind.find((p) => !p.until) ?? null;
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
  role: DanceRole | null = null,
): boolean {
  const candidates = division ? chips.filter((c) => c.division === division) : chips;
  if (!levels.length) return !division || candidates.length > 0;
  return candidates.some((c) => {
    const level = sideOf(c, role).level;
    return level !== null && levels.includes(level);
  });
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
export function levelScore(chips: Chip[], division: string | null, role: DanceRole | null = null): number | null {
  if (division) {
    const c = chips.find((x) => x.division === division);
    if (!c) return null;
    const side = sideOf(c, role);
    return c.top != null ? side.top : side.level ? levelRank[side.level] : null;
  }
  const known = chips.map((c) => sideOf(c, role)).filter((c) => c.level);
  if (!known.length) return null;
  const avgLevel = known.reduce((s, c) => s + levelRank[c.level!], 0) / known.length;
  const avgTop = known.reduce((s, c) => s + c.top, 0) / known.length;
  return avgLevel * 1000 + avgTop;
}
