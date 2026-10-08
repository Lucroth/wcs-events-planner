import { GoogleAuthProvider, signInWithPopup } from "firebase/auth";
import { collection, deleteDoc, doc, getDocs, query, serverTimestamp, setDoc, where } from "firebase/firestore";
import { auth, db } from "./firebase";

/**
 * A reader's own data under users/{uid}: starred events, and the connections of starred events
 * they want an email about when the daily fare check finds them cheaper (the sync's notify step).
 */
export interface Watch {
  eventId: string;
  eventName: string;
  kind: "flight" | "train";
  airline: string | null;
  from: string;
  to: string;
  date: string;
  time: string | null;
  price: number;
  currency: string;
}

/** The same key the sync's NotifyPublisher.LegKey builds, so a watch finds its leg in the fares. */
export const watchKey = (w: Watch): string =>
  w.kind === "train"
    ? `${w.eventId}|train|${w.from}|${w.to}|${w.date}|${w.time}`
    : `${w.eventId}|flight|${w.airline}|${w.from}|${w.to}|${w.date}`;

const uid = () => auth.currentUser?.uid;

export async function signIn(): Promise<void> {
  const { user } = await signInWithPopup(auth, new GoogleAuthProvider());
  // The address alerts go to; the rules only accept the account's own.
  await setDoc(doc(db, "users", user.uid), { email: user.email, name: user.displayName, updatedAt: serverTimestamp() }, { merge: true });
}

export async function favourites(): Promise<Set<string>> {
  const id = uid();
  if (!id) return new Set();
  return new Set((await getDocs(collection(db, "users", id, "favourites"))).docs.map((d) => d.id));
}

export async function setFavourite(eventId: string, on: boolean, name: string, dateFrom: string): Promise<void> {
  const id = uid()!;
  if (on) {
    await setDoc(doc(db, "users", id, "favourites", eventId), { name, dateFrom, createdAt: serverTimestamp() });
    return;
  }
  // Alerts are for starred events only: unstarring drops them too.
  await deleteDoc(doc(db, "users", id, "favourites", eventId));
  await Promise.all([...(await watches(eventId)).keys()].map((key) => deleteDoc(doc(db, "users", id, "watches", encodeURIComponent(key)))));
}

/** The reader's watches for one event, by watch key. */
export async function watches(eventId: string): Promise<Map<string, Watch>> {
  const id = uid();
  if (!id) return new Map();
  const snap = await getDocs(query(collection(db, "users", id, "watches"), where("eventId", "==", eventId)));
  return new Map(snap.docs.map((d) => [decodeURIComponent(d.id), d.data() as Watch]));
}

export async function setWatch(w: Watch, on: boolean): Promise<void> {
  const ref = doc(db, "users", uid()!, "watches", encodeURIComponent(watchKey(w)));
  await (on ? setDoc(ref, { ...w, createdAt: serverTimestamp() }) : deleteDoc(ref));
}
