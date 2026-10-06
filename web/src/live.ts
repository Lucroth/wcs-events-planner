import { doc, onSnapshot, type Timestamp } from "firebase/firestore";
import { addDays } from "./travel";
import { db } from "./firebase";
import { html, type Raw } from "./html";

/** One round of a live event, as the sync job follows it on scoring.dance (live/{id}). */
export interface LiveRound {
  name: string;
  day: string | null;
  time: string | null;
  /** scoring.dance's own code: 7 on the floor, 9–11 scoring, 99 finished... */
  status: number;
  label: string;
  roundId: number | null;
  /** Callbacks of a finished prelim or semi, one list per published table. */
  advanced?: { names: string[] }[] | null;
  /** Placings of a finished final. */
  placements?: { position: number; names: string }[] | null;
}

export interface Live {
  eventId: string;
  rounds: LiveRound[];
  updatedAt?: Timestamp;
}

/** Listeners of the page on screen; the router stops them before rendering the next one. */
const listeners: (() => void)[] = [];

export function stopListening(): void {
  listeners.splice(0).forEach((stop) => stop());
}

/** Calls back with the event's live state now and on every change, until the page is left. */
export function watchLive(eventId: string, update: (live: Live | undefined) => void): void {
  listeners.push(onSnapshot(doc(db, "live", eventId), (snap) => update(snap.exists() ? (snap.data() as Live) : undefined), () => update(undefined)));
}

/** The sync follows events from the day before they start to the day after they end. */
export const mayBeLive = (dateFrom: string, dateTo: string, today: string): boolean =>
  addDays(dateFrom, -1) <= today && today <= addDays(dateTo, 1);

const onFloor = (r: LiveRound) => r.status === 7;
const upNext = (r: LiveRound) => r.status === 5 || r.status === 6;
const done = (r: LiveRound) => r.status === 99;

/** One line for the event list: what is dancing now, else what is next. */
export function nowLine(live: Live | undefined): string {
  if (!live?.rounds.length) return "";
  const floor = live.rounds.filter(onFloor);
  if (floor.length) return `On the floor: ${floor.map((r) => r.name).join(", ")}`;
  const next = live.rounds.find(upNext) ?? live.rounds.find((r) => !done(r));
  if (next) return `Next: ${next.name}${next.time ? ` at ${next.time}` : ""}`;
  return "All competitions finished";
}

export function liveCard(live: Live | undefined, scoringId: string): Raw {
  if (!live?.rounds.length) {
    return html`<p class="muted">Waiting for the competition schedule from scoring.dance. This updates by itself.</p>`;
  }

  const days = new Map<string, LiveRound[]>();
  for (const r of live.rounds) days.set(r.day ?? "", [...(days.get(r.day ?? "") ?? []), r]);
  const updated = live.updatedAt?.toDate().toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" });
  const now = nowLine(live);

  return html`
    <p class="live-now ${live.rounds.some(onFloor) ? "dancing" : ""}">${now}</p>
    ${[...days].map(([day, rounds]) => html`
      ${day ? html`<h3>${day}</h3>` : ""}
      <ol class="live-rounds">${rounds.map((r) => round(r, scoringId))}</ol>`)}
    <p class="muted small">From scoring.dance, refreshed every minute${updated ? `; last change ${updated}` : ""}. Times are estimates.</p>`;
}

function round(r: LiveRound, scoringId: string): Raw {
  const state = onFloor(r) ? "floor" : done(r) ? "done" : r.status >= 8 ? "scoring" : upNext(r) ? "next" : "later";
  const link = r.roundId ? `https://scoring.dance/enUS/events/${scoringId}/results/${r.roundId}.html` : null;
  const detail = r.placements?.length
    ? html`<ol class="placings">${r.placements.map((p) => html`<li value="${p.position}">${p.names}</li>`)}</ol>`
    : r.advanced?.length
      ? html`${r.advanced.map((table) => html`<p class="small"><strong>Through (${table.names.length}):</strong> ${table.names.join(", ")}</p>`)}`
      : "";

  return html`
    <li class="live-round ${state}">
      <span class="when">${r.time ?? ""}</span>
      <span class="what">${r.name}</span>
      <span class="pill ${state}">${r.label}</span>
      ${detail ? html`<details><summary>${r.placements?.length ? "Results" : "Who got through"}</summary>${detail}${link ? html`<p class="small"><a href="${link}" target="_blank" rel="noopener">Full scores on scoring.dance</a></p>` : ""}</details>` : ""}
    </li>`;
}
