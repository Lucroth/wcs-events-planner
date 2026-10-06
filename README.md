# WCS Events Planner

A trip planner for Polish West Coast Swing dancers going to WSDC events. It has an event list for each year, and each event page shows passes, staff, links, schedules, J&J results, how hard each division is, flights or trains, and places to stay. Anyone can read it; admins sign in to edit.

```
GitHub Pages (web/, Vite + TS) ──reads──> Firestore <──writes── GitHub Actions (WcsEvents.Sync, .NET 10)
         admin edits ──writes──> info/*                       scrapes scoring.dance, WSDC registry,
                                                              Ryanair, Wizz; SQLite mirror kept as
                                                              an asset on the "mirror" release
```

## Firestore collections

| Collection | Written by | Contents |
|---|---|---|
| `years/{year}` | sync | one summary per year, which is all the event list reads |
| `events/{id}` | sync (scoring.dance ids), admin (`m-*` hand-made events) | dates, place, difficulty, J&J results, coordinates, airports, station |
| `info/{id}` | admin | passes, registration time, staff, links, schedules, venue, airports, `override` (corrected name/dates/place) |
| `flights/{eventId}_{origins}` | sync, daily | cheapest Ryanair/Wizz combinations from each set of Polish home airports |
| `admins/{uid}` | you, in the console | marks a Firebase Auth user as admin |

The access rules are in `firestore.rules`. Anyone may read. Only admins write `info` and `m-*` events. The sync writes with a service account, which bypasses the rules.

## Data sources and their limits

- **scoring.dance:** events, dates, ticket URL and results. It is mostly European, so other events need adding by hand.
- **WSDC registry:** the points each entrant held at the time, which is where difficulty comes from. Difficulty is the event's average for each division and role, ranked against all other events in thirds. Upcoming events use the previous edition with the same name.
- **Ryanair `farfnd` API:** reliable, but unofficial.
- **Wizz `timetable` API:** unofficial and guarded by bot protection that refuses quick repeated requests. It is called at most once every 10 s and is best-effort; datacenter IPs such as GitHub's may be refused entirely.
- **Trains (koleo.pl) and stays (Booking, Airbnb):** prefilled search links only. None of them has a public price API.
- **Passes, staff, links, schedules, venue:** no source publishes these, so the admin enters them.

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
