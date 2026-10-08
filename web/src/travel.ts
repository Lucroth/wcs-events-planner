/** Where a reader sets off from. The flight key must match HomeCities.Key in the sync job. */
export interface HomeCity {
  name: string;
  koleoSlug: string;
  airports: string[];
}

export const homeCities: HomeCity[] = [
  { name: "Warszawa", koleoSlug: "warszawa", airports: ["WAW", "WMI"] },
  { name: "Kraków", koleoSlug: "krakow", airports: ["KRK"] },
  { name: "Wrocław", koleoSlug: "wroclaw-glowny", airports: ["WRO"] },
  { name: "Poznań", koleoSlug: "poznan-glowny", airports: ["POZ"] },
  { name: "Gdańsk", koleoSlug: "gdansk", airports: ["GDN"] },
  { name: "Katowice", koleoSlug: "katowice", airports: ["KTW"] },
  { name: "Łódź", koleoSlug: "lodz", airports: ["LCJ"] },
  { name: "Szczecin", koleoSlug: "szczecin-glowny", airports: ["SZZ"] },
  { name: "Lublin", koleoSlug: "lublin-glowny", airports: ["LUZ"] },
  { name: "Rzeszów", koleoSlug: "rzeszow-glowny", airports: ["RZE"] },
  { name: "Bydgoszcz", koleoSlug: "bydgoszcz-glowna", airports: ["BZG"] },
  { name: "Białystok", koleoSlug: "bialystok", airports: [] },
  { name: "Toruń", koleoSlug: "torun", airports: ["BZG"] },
  { name: "Olsztyn", koleoSlug: "olsztyn", airports: ["SZY"] },
];

/** Polish airports the fare search flies from, for tooltips. */
export const polishAirports: Record<string, string> = {
  WAW: "Warsaw Chopin", WMI: "Warsaw Modlin", KRK: "Kraków", KTW: "Katowice", GDN: "Gdańsk", WRO: "Wrocław",
  POZ: "Poznań", LCJ: "Łódź", SZZ: "Szczecin", LUZ: "Lublin", RZE: "Rzeszów", BZG: "Bydgoszcz", SZY: "Olsztyn-Mazury",
  RDO: "Radom", IEG: "Zielona Góra",
};

export const findCity = (name: string | null): HomeCity => homeCities.find((c) => c.name === name) ?? homeCities[0];

/** A city with no airport of its own flies from Warsaw. */
export const flightOrigins = (c: HomeCity): string[] => (c.airports.length ? c.airports : ["WAW", "WMI"]);

export const flightKey = (c: HomeCity): string => flightOrigins(c).join("-");

const pad = (n: number) => String(n).padStart(2, "0");

/** yyyy-MM-dd shifted by whole days, in UTC so no timezone moves the date. */
export function addDays(iso: string, days: number): string {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

export const ryanairUrl = (from: string, to: string, date: string, adults: number) =>
  `https://www.ryanair.com/pl/pl/trip/flights/select?adults=${adults}&teens=0&children=0&infants=0&dateOut=${date}&isReturn=false&originIata=${from}&destinationIata=${to}`;

export const wizzUrl = (from: string, to: string, date: string, adults: number) =>
  `https://www.wizzair.com/en-gb/booking/select-flight/${from}/${to}/${date}/null/${adults}/0/0/null`;

/** Google parses a query naming one place on each side; a list of airport codes leaves "to" empty. */
export const googleFlightsUrl = (from: string, to: string, outDate: string, backDate: string) =>
  "https://www.google.com/travel/flights?hl=en&curr=PLN&q=" +
  encodeURIComponent(`Flights from ${from} to ${to} on ${outDate} through ${backDate}`);

/** koleo's timetable search; `hour` is the earliest departure. */
export function koleoUrl(fromSlug: string, toSlug: string, date: string, hour = 6): string {
  const [y, m, d] = date.split("-");
  return `https://koleo.pl/rozklad-pkp/${fromSlug}/${toSlug}/${d}-${m}-${y}_${pad(hour)}:00/all/all`;
}

export const googleTransitUrl = (from: string, to: string) =>
  `https://www.google.com/maps/dir/?api=1&travelmode=transit&origin=${encodeURIComponent(from)}&destination=${encodeURIComponent(to)}`;

export const mapsUrl = (q: string) => `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(q)}`;

/**
 * Booking around the venue, sorted by distance, so the cheap end of the list is also the near end;
 * without coordinates, the place name sorted by price.
 */
export function bookingUrl(place: string, coords: { lat: number; lng: number } | null, checkIn: string, checkOut: string, guests: number): string {
  const where = coords
    ? `latitude=${coords.lat}&longitude=${coords.lng}&dest_type=latlong&order=distance_from_search`
    : "order=price";
  return `https://www.booking.com/searchresults.en-gb.html?ss=${encodeURIComponent(place)}&${where}&checkin=${checkIn}&checkout=${checkOut}&group_adults=${guests}&no_rooms=1&group_children=0`;
}

/** Airbnb within roughly 2 km of the venue when its coordinates are known. */
export function airbnbUrl(place: string, coords: { lat: number; lng: number } | null, checkIn: string, checkOut: string, guests: number): string {
  let box = "";
  if (coords) {
    const dLat = 0.018;
    const dLng = dLat / Math.cos((coords.lat * Math.PI) / 180);
    box = `&ne_lat=${coords.lat + dLat}&ne_lng=${coords.lng + dLng}&sw_lat=${coords.lat - dLat}&sw_lng=${coords.lng - dLng}&search_by_map=true`;
  }
  return `https://www.airbnb.com/s/${encodeURIComponent(place)}/homes?checkin=${checkIn}&checkout=${checkOut}&adults=${guests}${box}`;
}
