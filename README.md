# WCS Events Planner

A trip planner for Polish West Coast Swing dancers going to WSDC events. It has an event list for each year, and each event page shows passes, staff, links, schedules, J&J results, how hard each division is, flights or trains, and places to stay. Anyone can read it; admins sign in to edit.

```
GitHub Pages (web/, Vite + TS) ──reads──> Firestore <──writes── GitHub Actions (WcsEvents.Sync, .NET 10)
         admin edits ──writes──> info/*                       reads the WSDC calendar, scoring.dance,
                                                              the WSDC registry, Ryanair, Wizz, koleo;
                                                              SQLite mirror kept as an asset on the
                                                              "mirror" release
```

## Firestore collections

| Collection | Written by | Contents |
|---|---|---|
| `years/{year}` | sync | one summary per year, which is all the event list reads |
| `events/{id}` | sync (scoring.dance ids; `x{id}` and `w-{name}-{year}` for calendar editions scoring.dance lacks), admin (`m-*` hand-made events) | dates, place, difficulty, J&J results, coordinates, airports, station |
| `info/{id}` | admin | passes, registration time, staff, links, schedules, venue, airports, `override` (corrected name/dates/place) |
| `flights/{eventId}_{origins}`, `trains/{eventId}_{city}` | sync, daily | Ryanair/Wizz flights from each set of Polish home airports; koleo fares for Polish events within 35 days |
| `live/{eventId}` | sync, every minute during an event | the competition schedule and who got through each round |
| `users/{uid}` (`favourites`, `watches`) | the reader | starred events and the flights/trains they want a price-drop email about |
| `admins/{uid}` | you, in the console | marks a Firebase Auth user as admin |

The access rules are in `firestore.rules`. Anyone may read. Only admins write `info` and `m-*` events. The sync writes with a service account, which bypasses the rules.

## Data sources and their limits

- **WSDC calendar (worldsdc.com/events):** the authority on which upcoming events are sanctioned, with exact dates, the organiser's website and the venue's street address (read from the page's JSON-LD). An edition scoring.dance already lists takes the calendar's dates; one it lacks becomes an event of its own, borrowing the field strengths of the series' latest edition. If the page cannot be read, nothing it added is removed.
- **scoring.dance:** results, and the events it knows. An organiser has to set an event up there, often months after the WSDC lists it.
- **WSDC registry:** the points each entrant held at the time, which is where difficulty comes from. Difficulty is the event's average for each division and role, ranked against all other events in thirds. Upcoming events use the previous edition with the same name.
- **Ryanair `farfnd` API:** reliable, but unofficial.
- **Wizz `timetable` API:** unofficial, with bot protection that refuses quick repeated requests (once every 10 s) and any request carrying the cookie it sets (cookies are off for this client). Best-effort.
- **Trains (koleo.pl):** fares through its private API. **Stays (Booking, Airbnb):** prefilled search links only; neither has a public price API.
- **Passes, staff, links, schedules, venue:** no source publishes these as data. They are read off the organisers' sites and DanceApp ticket pages into `data/autofill.json`; the `autofill` sync step copies them into `info/{id}`, only into fields the admin has left empty, once per entry. The event pages mark them as not yet reviewed until the admin saves.

## One-time setup

1. In Firebase **Authentication**, enable Email/Password and add yourself as a user. Then go to Settings → Authorized domains and add `lucroth.github.io`.
2. Create a **Firestore** database (production mode). Then deploy the rules:
   ```bash
   npx firebase-tools login
   npx firebase-tools deploy --only firestore:rules
   ```
3. In **Firestore**, create the document `admins/<your auth uid>` with any field.
4. In Project settings → Service accounts, choose **Generate new private key**. Store the JSON as a GitHub secret, then delete the file:
   ```bash
   gh secret set FIREBASE_SERVICE_ACCOUNT --repo Lucroth/wcs-events-planner < key.json
   ```
5. Set the GitHub repository variables `FIREBASE_PROJECT_ID`, `FIREBASE_API_KEY` and `FIREBASE_APP_ID`. They come from the web app config, which is public by design.
6. Run the **Sync** workflow with `scoring publish flights`. If no mirror exists yet, start with `sweep` and run it until it finishes; each run is capped at 4 h.

## Local development

```bash
cd web && cp .env.example .env.local   # or set VITE_USE_EMULATORS=true for the emulators
npm install && npm run dev
```

```bash
cd WcsEvents.Sync
FIREBASE_PROJECT_ID=... GOOGLE_APPLICATION_CREDENTIALS=key.json dotnet run -- publish
```

The emulators (`npx firebase-tools emulators:start --only firestore,auth`) need Java 21. With `FIRESTORE_EMULATOR_HOST=127.0.0.1:8080`, the sync writes to them instead of production.

Tests: `dotnet test` and `cd web && npm test`.
