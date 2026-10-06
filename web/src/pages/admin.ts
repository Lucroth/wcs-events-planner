import { signInWithEmailAndPassword, signOut } from "firebase/auth";
import { auth, getEvent, getInfo, getInfosForYear, getManualEvents, getYear, newManualId, saveInfo, saveManualEvent } from "../firebase";
import { range } from "../format";
import { html, safeUrl, type Raw } from "../html";
import { applyOverride, type Info, type Override, type Pass, type ScrapedEvent } from "../model";

const blankPassRows = 3;

export function loginPage(failed: boolean): Raw {
  return html`
    <h1>Admin</h1>
    ${failed ? html`<p class="error">Wrong email or password, or this account is not an admin.</p>` : ""}
    <form id="login" class="stack narrow">
      <label>Email <input type="email" name="email" autocomplete="username" required /></label>
      <label>Password <input type="password" name="password" autocomplete="current-password" required /></label>
      <button type="submit">Sign in</button>
    </form>`;
}

export function wireLogin(onDone: (ok: boolean) => void): void {
  document.getElementById("login")?.addEventListener("submit", async (ev) => {
    ev.preventDefault();
    const data = new FormData(ev.target as HTMLFormElement);
    try {
      await signInWithEmailAndPassword(auth, String(data.get("email")), String(data.get("password")));
      onDone(true);
    } catch {
      onDone(false);
    }
  });
}

export const logout = () => signOut(auth);

export async function dashboardPage(year: number): Promise<Raw> {
  const [summary, infos, manual] = await Promise.all([getYear(year), getInfosForYear(year), getManualEvents(year)]);
  const rows = [...(summary?.events ?? []), ...manual.map((m) => ({ ...m, chips: [] }))]
    .map((e) => applyOverride(e, infos.get(e.id)))
    .filter((e) => e.isWsdc || infos.has(e.id))
    .sort((a, b) => a.dateFrom.localeCompare(b.dateFrom));
  const thisYear = new Date().getFullYear();

  const check = (ok: boolean) => html`<td class="${ok ? "yes" : "no"}">${ok ? "✓" : "—"}</td>`;

  return html`
    <h1>Admin</h1>
    <section class="card">
      <p>Scraping runs in GitHub Actions: scoring.dance weekly, flights daily, WSDC registry monthly.
        <a href="https://github.com/Lucroth/wcs-events-planner/actions" target="_blank" rel="noopener">Run or check them</a>.</p>
      <p><a class="button" href="#/admin/new">+ Add event by hand</a></p>
    </section>
    <section class="card">
      <h2>${year}: what's missing</h2>
      <nav class="years">${[thisYear + 1, thisYear, thisYear - 1].map((y) => html`<a href="#/admin?year=${y}" class="${y === year ? "chip on" : "chip"}">${y}</a>`)}</nav>
      <table>
        <thead><tr><th>Dates</th><th>Event</th><th>Prices</th><th>Registration</th><th>Staff</th><th>Links</th><th>Venue</th><th>Schedule</th><th></th></tr></thead>
        <tbody>
          ${rows.map((e) => {
            const i = infos.get(e.id);
            return html`
              <tr>
                <td>${range(e.dateFrom, e.dateTo)}</td>
                <td><a href="#/event/${e.id}">${e.name}</a></td>
                ${check(!!i?.passes?.length)}
                ${check(!!i?.registrationOpens)}
                ${check(!!i?.staff?.length)}
                ${check(!!(i?.facebookUrl || i?.websiteUrl))}
                ${check(i?.lat != null)}
                ${check(!!i?.eventSchedule)}
                <td><a href="#/admin/event/${e.id}">Edit</a></td>
              </tr>`;
          })}
        </tbody>
      </table>
    </section>`;
}

/** `id` null means a new hand-made event. */
export async function editPage(id: string | null): Promise<Raw> {
  const scraped = id ? await getEvent(id) : undefined;
  if (id && !scraped) return html`<h1>Event not found</h1>`;
  const info = (id ? await getInfo(id) : undefined) ?? {};
  const e = scraped ? applyOverride(scraped, info) : null;
  const passes: (Pass | null)[] = [...(info.passes ?? []), ...Array(blankPassRows).fill(null)];
  const v = (s: string | null | undefined) => s ?? "";

  return html`
    <h1>${e ? e.name : "New event"}</h1>
    ${scraped && !scraped.manual ? html`<p class="muted small">From scoring.dance. Changing the name, dates or place stores a correction that the weekly sync will not overwrite.</p>` : ""}
    <p class="error" id="form-error" hidden></p>
    <form id="edit" class="stack edit">
      <fieldset>
        <legend>Event</legend>
        <label>Name <input name="name" required value="${v(e?.name)}" /></label>
        <div class="row">
          <label>From <input type="date" name="dateFrom" required value="${v(e?.dateFrom)}" /></label>
          <label>To <input type="date" name="dateTo" value="${v(e?.dateTo)}" /></label>
        </div>
        <div class="row">
          <label>City <input name="city" required value="${v(e?.city)}" /></label>
          <label>Country (in English, e.g. Poland, Germany) <input name="country" required value="${v(e?.country)}" /></label>
        </div>
        <label class="check"><input type="checkbox" name="isWsdc" ${e ? (e.isWsdc ? "checked" : "") : "checked"} /> WSDC event (awards points)</label>
      </fieldset>

      <fieldset>
        <legend>Links</legend>
        ${(["websiteUrl:Website", "facebookUrl:Facebook", "instagramUrl:Instagram", "streamUrl:Livestream", "polishGroupUrl:Polish FB group"] as const).map((pair) => {
          const [key, label] = pair.split(":") as [keyof Info, string];
          return html`<label>${label} <input type="url" name="${key}" value="${v(info[key] as string | undefined)}" /></label>`;
        })}
      </fieldset>

      <fieldset>
        <legend>Registration &amp; passes</legend>
        <label>Registration opens (event local time) <input type="datetime-local" name="registrationOpens" value="${v(info.registrationOpens)}" /></label>
        <table class="passes">
          <thead><tr><th>Kind</th><th>Tier</th><th>Price</th><th>Currency</th><th>On sale until</th></tr></thead>
          <tbody>
            ${passes.map((p, n) => html`
              <tr>
                <td><select name="pass-kind-${n}"><option value="Full" ${p?.kind === "Party" ? "" : "selected"}>Full pass</option><option value="Party" ${p?.kind === "Party" ? "selected" : ""}>Party pass</option></select></td>
                <td><input name="pass-tier-${n}" placeholder="Early bird" value="${v(p?.tier)}" /></td>
                <td><input type="number" step="0.01" min="0" name="pass-price-${n}" value="${p ? p.price : ""}" /></td>
                <td><input name="pass-currency-${n}" size="4" value="${p?.currency ?? "EUR"}" /></td>
                <td><input type="date" name="pass-until-${n}" value="${v(p?.until)}" /></td>
              </tr>`)}
          </tbody>
        </table>
        <p class="muted small">No date means it's sold until the door. Rows without a tier or price are skipped. Save to get more empty rows.</p>
      </fieldset>

      <fieldset>
        <legend>Venue &amp; travel</legend>
        <label>Venue name <input name="venueName" value="${v(info.venueName)}" /></label>
        <label>Address <input name="venueAddress" value="${v(info.venueAddress)}" /></label>
        <div class="row">
          <label>Latitude <input type="number" step="any" min="-90" max="90" name="lat" value="${info.lat ?? ""}" /></label>
          <label>Longitude <input type="number" step="any" min="-180" max="180" name="lng" value="${info.lng ?? ""}" /></label>
        </div>
        <p class="muted small">Coordinates from Google Maps (right-click the venue). Without them, accommodation and flights are worked out from the city centre. Travel picks up changes on the next daily run.</p>
        <label>Airports to fly to (IATA codes; empty = 3 nearest) <input name="airports" placeholder="BGY, MXP" value="${(info.airports ?? []).join(", ")}" /></label>
      </fieldset>

      <fieldset>
        <legend>Staff &amp; schedules</legend>
        <label>Staff (one per line) <textarea name="staff" rows="6">${(info.staff ?? []).join("\n")}</textarea></label>
        <label>Event schedule (text or a link) <textarea name="eventSchedule" rows="6">${v(info.eventSchedule)}</textarea></label>
        <label>Competition schedule (text or a link) <textarea name="compSchedule" rows="6">${v(info.compSchedule)}</textarea></label>
      </fieldset>

      <button type="submit">Save</button>
    </form>`;
}

/** Reads the form; returns an error message instead when something would not be safe to show. */
export function readForm(form: HTMLFormElement, scraped: ScrapedEvent | undefined): { info: Info; core: Required<Override> } | string {
  const data = new FormData(form);
  const s = (k: string) => String(data.get(k) ?? "").trim();
  const opt = (k: string) => s(k) || null;
  const num = (k: string) => (s(k) === "" ? null : Number(s(k)));

  for (const k of ["websiteUrl", "facebookUrl", "instagramUrl", "streamUrl", "polishGroupUrl"]) {
    if (s(k) && !safeUrl(s(k))) return `${k}: links must start with http:// or https://`;
  }
  const dateFrom = s("dateFrom");
  const dateTo = s("dateTo") || dateFrom;
  if (dateTo < dateFrom) return "The event ends before it starts.";
  const lat = num("lat");
  const lng = num("lng");
  if ((lat === null) !== (lng === null)) return "Give both coordinates or neither.";

  const passes: Pass[] = [];
  for (let n = 0; data.has(`pass-tier-${n}`); n++) {
    const tier = s(`pass-tier-${n}`);
    const price = num(`pass-price-${n}`);
    if (!tier || price === null || Number.isNaN(price)) continue;
    passes.push({
      kind: s(`pass-kind-${n}`) === "Party" ? "Party" : "Full",
      tier,
      price,
      currency: (s(`pass-currency-${n}`) || "EUR").toUpperCase(),
      until: opt(`pass-until-${n}`),
    });
  }

  const core: Required<Override> = {
    name: s("name"),
    dateFrom,
    dateTo,
    city: s("city"),
    country: s("country"),
    isWsdc: data.get("isWsdc") === "on",
  };

  // A correction is stored only for what differs from what the sync publishes.
  let override: Override | null = null;
  if (scraped && !scraped.manual) {
    const diff = (Object.keys(core) as (keyof Override)[]).filter((k) => core[k] !== (scraped[k as keyof ScrapedEvent] ?? ""));
    override = diff.length ? Object.fromEntries(diff.map((k) => [k, core[k]])) : null;
  }

  return {
    core,
    info: {
      year: Number(dateFrom.slice(0, 4)),
      override,
      websiteUrl: opt("websiteUrl"),
      facebookUrl: opt("facebookUrl"),
      instagramUrl: opt("instagramUrl"),
      streamUrl: opt("streamUrl"),
      polishGroupUrl: opt("polishGroupUrl"),
      registrationOpens: opt("registrationOpens"),
      venueName: opt("venueName"),
      venueAddress: opt("venueAddress"),
      lat,
      lng,
      airports: s("airports").split(/[\s,;]+/).map((a) => a.toUpperCase()).filter((a) => /^[A-Z]{3}$/.test(a)),
      staff: s("staff").split("\n").map((x) => x.trim()).filter(Boolean),
      eventSchedule: opt("eventSchedule"),
      compSchedule: opt("compSchedule"),
      passes,
    },
  };
}

export function wireEdit(id: string | null, done: (id: string) => void): void {
  const form = document.getElementById("edit") as HTMLFormElement | null;
  form?.addEventListener("submit", async (ev) => {
    ev.preventDefault();
    const error = document.getElementById("form-error")!;
    const scraped = id ? await getEvent(id) : undefined;
    const read = readForm(form, scraped);
    if (typeof read === "string") {
      error.textContent = read;
      error.hidden = false;
      return;
    }

    try {
      const eventId = id ?? newManualId();
      if (!scraped || scraped.manual) {
        await saveManualEvent({
          id: eventId,
          ...read.core,
          year: Number(read.core.dateFrom.slice(0, 4)),
          ticketUrl: null,
          coords: read.info.lat != null && read.info.lng != null ? { lat: read.info.lat, lng: read.info.lng } : null,
          airports: read.info.airports?.map((iata) => ({ iata, name: iata })) ?? null,
          station: null,
          strengths: [],
          strengthsFrom: null,
          results: [],
        });
      }
      await saveInfo(eventId, read.info);
      done(eventId);
    } catch (e) {
      error.textContent = `Could not save: ${(e as Error).message}`;
      error.hidden = false;
    }
  });
}
