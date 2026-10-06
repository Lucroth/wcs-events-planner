import { initializeApp } from "firebase/app";
import { connectAuthEmulator, getAuth } from "firebase/auth";
import {
  collection,
  deleteDoc,
  connectFirestoreEmulator,
  doc,
  getDoc,
  getDocs,
  getFirestore,
  query,
  serverTimestamp,
  setDoc,
  where,
} from "firebase/firestore";
import type { Flights, Info, ScrapedEvent, Trains, YearSummary } from "./model";

const app = initializeApp({
  apiKey: import.meta.env.VITE_FIREBASE_API_KEY,
  authDomain: import.meta.env.VITE_FIREBASE_AUTH_DOMAIN,
  projectId: import.meta.env.VITE_FIREBASE_PROJECT_ID,
  appId: import.meta.env.VITE_FIREBASE_APP_ID,
});

export const db = getFirestore(app);
export const auth = getAuth(app);

if (import.meta.env.VITE_USE_EMULATORS === "true") {
  connectFirestoreEmulator(db, "127.0.0.1", 8080);
  connectAuthEmulator(auth, "http://127.0.0.1:9099", { disableWarnings: true });
}

const C = {
  events: "events",
  years: "years",
  info: "info",
  flights: "flights",
  trains: "trains",
  schedules: "schedules",
  admins: "admins",
} as const;

async function read<T>(path: string, id: string): Promise<T | undefined> {
  const snap = await getDoc(doc(db, path, id));
  return snap.exists() ? (snap.data() as T) : undefined;
}

export const getYear = (year: number) => read<YearSummary>(C.years, String(year));

export const getEvent = (id: string) => read<ScrapedEvent>(C.events, id);

export const getInfo = (id: string) => read<Info>(C.info, id);

/** Fares for an event from every Polish airport the sync searched, one document per home city. */
export const getAllFlights = async (eventId: string) =>
  (await getDocs(query(collection(db, C.flights), where("eventId", "==", eventId)))).docs.map((d) => d.data() as Flights);

export const getTrains = (eventId: string, citySlug: string) => read<Trains>(C.trains, `${eventId}_${citySlug}`);

/** Cheapest return fare per event for one home city, flights and trains together, keyed by event id. */
export async function getCheapestTravel(flightKey: string, citySlug: string): Promise<Map<string, number>> {
  const [flights, trains] = await Promise.all([
    getDocs(query(collection(db, C.flights), where("key", "==", flightKey))),
    getDocs(query(collection(db, C.trains), where("city", "==", citySlug))),
  ]);
  const cheapest = new Map<string, number>();
  for (const d of [...flights.docs, ...trains.docs]) {
    const data = d.data() as { eventId?: string; cheapest?: number | null };
    if (data.eventId && data.cheapest != null) cheapest.set(data.eventId, data.cheapest);
  }
  return cheapest;
}

let rates: Promise<Map<string, number>> | undefined;

/** PLN per unit of each currency, NBP table A; fetched once per page load. */
export function plnRates(): Promise<Map<string, number>> {
  rates ??= fetch("https://api.nbp.pl/api/exchangerates/tables/a/?format=json")
    .then((r) => r.json() as Promise<{ rates: { code: string; mid: number }[] }[]>)
    .then((t) => new Map([["PLN", 1], ...t[0].rates.map((r) => [r.code, r.mid] as [string, number])]))
    .catch(() => new Map([["PLN", 1]]));
  return rates;
}

/** Admin data for one year's events, keyed by event id. */
export async function getInfosForYear(year: number): Promise<Map<string, Info>> {
  const snap = await getDocs(query(collection(db, C.info), where("year", "==", year)));
  return new Map(snap.docs.map((d) => [d.id, d.data() as Info]));
}

/** Events the admin added by hand: the sync's year summaries do not know them. */
export async function getManualEvents(year: number): Promise<ScrapedEvent[]> {
  const snap = await getDocs(query(collection(db, C.events), where("manual", "==", true), where("year", "==", year)));
  return snap.docs.map((d) => ({ ...(d.data() as ScrapedEvent), id: d.id }));
}

export const isAdmin = async (uid: string) => (await getDoc(doc(db, C.admins, uid))).exists();

export const saveInfo = (id: string, info: Info) => setDoc(doc(db, C.info, id), { ...info, updatedAt: serverTimestamp() });

export const saveManualEvent = (e: ScrapedEvent) => setDoc(doc(db, C.events, e.id), { ...e, manual: true, updatedAt: serverTimestamp() });

export type ScheduleKind = "event" | "comp";

export const getScheduleImage = async (id: string, kind: ScheduleKind) =>
  (await read<{ image: string }>(C.schedules, `${id}_${kind}`))?.image;

export const saveScheduleImage = (id: string, kind: ScheduleKind, image: string | null) =>
  image ? setDoc(doc(db, C.schedules, `${id}_${kind}`), { image, updatedAt: serverTimestamp() }) : deleteDoc(doc(db, C.schedules, `${id}_${kind}`));

export const newManualId = () => `m-${doc(collection(db, C.events)).id}`;
