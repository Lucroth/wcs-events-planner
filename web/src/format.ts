import type { Difficulty } from "./model";

const day = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", timeZone: "UTC" });
const full = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" });
const weekday = new Intl.DateTimeFormat("en-GB", { weekday: "short", day: "numeric", month: "short", timeZone: "UTC" });
const monthName = new Intl.DateTimeFormat("en-GB", { month: "long", timeZone: "UTC" });

const utc = (iso: string) => new Date(`${iso.slice(0, 10)}T00:00:00Z`);

export const today = (): string => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};

export const date = (iso: string | null | undefined) => (iso ? full.format(utc(iso)) : "?");

export const short = (iso: string) => weekday.format(utc(iso));

export const month = (m: number) => monthName.format(new Date(Date.UTC(2000, m - 1, 1)));

export function range(from: string, to: string): string {
  if (from === to) return day.format(utc(from));
  if (from.slice(0, 7) === to.slice(0, 7)) return `${utc(from).getUTCDate()}–${day.format(utc(to))}`;
  return `${day.format(utc(from))} – ${day.format(utc(to))}`;
}

/** "2026-06-01T20:00" as written, without converting: it is the event's own local time. */
export function localDateTime(value: string): string {
  const [d, t] = value.split("T");
  return t ? `${date(d)}, ${t.slice(0, 5)}` : date(d);
}

export function money(amount: number, currency: string): string {
  return `${new Intl.NumberFormat("en-GB", { maximumFractionDigits: amount % 1 ? 2 : 0, minimumFractionDigits: amount % 1 ? 2 : 0 }).format(amount)} ${currency}`;
}

export const duration = (minutes: number) => `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, "0")}m`;

export const level = (d: Difficulty) => ({ Easy: "easy", Medium: "medium", Hard: "hard" })[d];
