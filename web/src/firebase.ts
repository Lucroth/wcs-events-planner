import { initializeApp } from "firebase/app";
import { connectAuthEmulator, getAuth } from "firebase/auth";
import {
  collection,
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
import type { Flights, Info, ScrapedEvent, YearSummary } from "./model";

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
  admins: "admins",
} as const;

async function read<T>(path: string, id: string): Promise<T | undefined> {
  const snap = await getDoc(doc(db, path, id));
  return snap.exists() ? (snap.data() as T) : undefined;
}

export const getYear = (year: number) => read<YearSummary>(C.years, String(year));

export const getEvent = (id: string) => read<ScrapedEvent>(C.events, id);

export const getInfo = (id: string) => read<Info>(C.info, id);

export const getFlights = (eventId: string, key: string) => read<Flights>(C.flights, `${eventId}_${key}`);

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

export const newManualId = () => `m-${doc(collection(db, C.events)).id}`;
