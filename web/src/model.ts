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
    chips: { division: string; level: Difficulty | null }[];
  }[];
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
  origins: string[];
  destinations: string[];
  currency: string;
  combos: { out: Leg; back: Leg; perPerson: number }[];
  out: Leg[];
  back: Leg[];
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
  chips: { division: string; level: Difficulty | null }[],
  division: string | null,
  levels: Difficulty[],
): boolean {
  const candidates = division ? chips.filter((c) => c.division === division) : chips;
  if (!levels.length) return !division || candidates.length > 0;
  return candidates.some((c) => c.level !== null && levels.includes(c.level));
}
