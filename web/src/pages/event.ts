import { getEvent, getFlights, getInfo } from "../firebase";
import { date, duration, level, localDateTime, money, range, short, today } from "../format";
import { html, safeUrl, type Raw } from "../html";
import { applyOverride, currentPass, type Flights, type Info, type Leg, type Pass, type ScrapedEvent } from "../model";
import {
  addDays,
  airbnbUrl,
  bookingUrl,
  findCity,
  flightKey,
  flightOrigins,
  googleFlightsUrl,
  googleTransitUrl,
  homeCities,
  koleoUrl,
  mapsUrl,
  ryanairUrl,
  wizzUrl,
  type HomeCity,
} from "../travel";

export async function eventPage(id: string, params: URLSearchParams, admin: boolean): Promise<Raw> {
  const [scraped, info] = await Promise.all([getEvent(id), getInfo(id)]);
  if (!scraped) {
    return html`<h1>Event not found</h1><p><a href="#/">Back to the list</a></p>`;
  }

  const e = applyOverride(scraped, info);
  const now = today();
  const city = findCity(params.get("from"));
  const people = Math.min(12, Math.max(1, Number(params.get("people")) || 1));
  const upcoming = e.dateTo >= now;
  const flights = upcoming && e.country !== "Poland" ? await getFlights(id, flightKey(city)) : undefined;

  return html`
    <p class="back"><a href="#/year/${e.dateFrom.slice(0, 4)}">← ${e.dateFrom.slice(0, 4)} events</a></p>
    <h1>${e.name}</h1>
    <p class="lead">
      ${range(e.dateFrom, e.dateTo)} ${e.dateFrom.slice(0, 4)} · ${[e.city, e.country].filter(Boolean).join(", ")}
      ${info?.venueName ? html` · <a href="${mapsUrl(info.venueAddress || info.venueName)}" target="_blank" rel="noopener">${info.venueName}</a>` : ""}
      ${e.isWsdc ? html`<span class="tag">WSDC</span>` : ""}
      ${admin ? html`<a class="button small" href="#/admin/event/${id}">Edit</a>` : ""}
    </p>
    <section class="links">${links(scraped, info)}</section>
    <div class="grid">
      ${passesCard(info, now)}
      <section class="card">
        <h2>Staff</h2>
        ${info?.staff?.length ? html`<ul class="staff">${info.staff.map((s) => html`<li>${s}</li>`)}</ul>` : html`<p class="muted">No staff list yet.</p>`}
      </section>
    </div>
    <div class="grid">
      ${schedule("Event schedule", info?.eventSchedule)}
      ${schedule("Competition schedule", info?.compSchedule)}
    </div>
    ${strengthCard(scraped)}
    ${resultsCard(scraped)}
    ${upcoming ? travelCard(scraped, e, info, city, people, flights) : ""}`;
}

function links(e: ScrapedEvent, info: Info | undefined): Raw {
  const list: [string, string | null][] = [
    ["Website", safeUrl(info?.websiteUrl) ?? safeUrl(e.ticketUrl)],
    ["Facebook", safeUrl(info?.facebookUrl)],
    ["Instagram", safeUrl(info?.instagramUrl)],
    ["Livestream", safeUrl(info?.streamUrl)],
    ["Polish FB group", safeUrl(info?.polishGroupUrl)],
    ["scoring.dance", e.manual ? null : `https://scoring.dance/enUS/events/${e.id}/results/`],
  ];
  return html`${list.filter(([, u]) => u).map(([label, u]) => html`<a class="button" href="${u}" target="_blank" rel="noopener">${label}</a>`)}`;
}

function passesCard(info: Info | undefined, now: string): Raw {
  const passes = [...(info?.passes ?? [])].sort((a, b) => a.kind.localeCompare(b.kind) || (a.until ?? "9999").localeCompare(b.until ?? "9999"));
  const current = new Set<Pass | null>([currentPass(passes, "Full", now), currentPass(passes, "Party", now)]);
  const opens = info?.registrationOpens;

  return html`
    <section class="card">
      <h2>Passes</h2>
      ${opens ? html`<p>Registration ${opens.slice(0, 10) > now ? "opens" : "opened"} <strong>${localDateTime(opens)}</strong> (event local time)</p>` : ""}
      ${passes.length
        ? html`<table>
            <thead><tr><th></th><th>Tier</th><th class="num">Price</th><th>Until</th></tr></thead>
            <tbody>
              ${passes.map((p) => html`
                <tr class="${current.has(p) ? "current" : p.until && p.until < now ? "gone" : ""}">
                  <td>${p.kind} pass</td>
                  <td>${p.tier} ${current.has(p) ? html`<span class="tag">now</span>` : ""}</td>
                  <td class="num">${money(p.price, p.currency)}</td>
                  <td>${p.until ? date(p.until) : "—"}</td>
                </tr>`)}
            </tbody>
          </table>`
        : html`<p class="muted">No pricing yet.</p>`}
    </section>`;
}

function schedule(title: string, text: string | null | undefined): Raw {
  const url = safeUrl(text);
  return html`
    <section class="card">
      <h2>${title}</h2>
      ${!text?.trim() ? html`<p class="muted">Not published yet.</p>` : url ? html`<p><a href="${url}" target="_blank" rel="noopener">See the schedule</a></p>` : html`<pre class="schedule">${text}</pre>`}
    </section>`;
}

function strengthCard(e: ScrapedEvent): Raw {
  if (!e.strengths.length) {
    return html`<section class="card"><h2>Competition level</h2><p class="muted">No data yet: no results on scoring.dance for this event or an earlier edition.</p></section>`;
  }
  return html`
    <section class="card">
      <h2>Competition level</h2>
      ${e.strengthsFrom ? html`<p class="muted">Based on the previous edition: <a href="#/event/${e.strengthsFrom.id}">${e.strengthsFrom.name} (${date(e.strengthsFrom.dateFrom)})</a>.</p>` : ""}
      <p class="muted small">WSDC points competitors held in the division when they danced. <strong>Top 25%</strong> is the average of the strongest quarter of the field, roughly who you have to beat to make the final; the level is ranked by it ("hard" = top third of all events in that division). <strong>Avg</strong> covers everyone, so it mostly shows how many entrants have no points yet.</p>
      <table>
        <thead><tr><th>Division</th><th></th><th class="num">Dancers</th><th class="num">Top 25% avg</th><th class="num">Avg</th><th class="num">Median</th><th>Level</th></tr></thead>
        <tbody>
          ${e.strengths.map((s) => html`
            <tr>
              <td>${s.division}</td>
              <td>${s.role === "Leader" ? "Leaders" : "Followers"}</td>
              <td class="num">${s.fieldSize}</td>
              <td class="num"><strong>${s.topQuartileAverage != null ? s.topQuartileAverage.toFixed(1) : "—"}</strong></td>
              <td class="num">${s.averagePoints.toFixed(1)}</td>
              <td class="num">${s.medianPoints}</td>
              <td>${s.difficulty ? html`<span class="chip diff-${level(s.difficulty)}">${level(s.difficulty)}</span>` : ""}</td>
            </tr>`)}
        </tbody>
      </table>
    </section>`;
}

function resultsCard(e: ScrapedEvent): Raw {
  if (!e.results.length) return html``;
  return html`
    <section class="card">
      <h2>Jack &amp; Jill results</h2>
      <div class="results">
        ${e.results.map((r) => html`
          <div>
            <h3><a href="https://scoring.dance/enUS/events/${e.id}/results/${r.roundId}.html" target="_blank" rel="noopener">${r.roundName}</a></h3>
            <ol>${r.places.map((p) => html`<li value="${p.position}">${p.names}</li>`)}</ol>
          </div>`)}
      </div>
    </section>`;
}

function travelCard(scraped: ScrapedEvent, e: { dateFrom: string; dateTo: string; city: string | null; country: string | null }, info: Info | undefined, city: HomeCity, people: number, flights: Flights | undefined): Raw {
  const coords = info?.lat != null && info?.lng != null ? { lat: info.lat, lng: info.lng } : scraped.coords;
  const place = info?.venueAddress || [e.city, e.country].filter(Boolean).join(", ");
  const checkOut = addDays(e.dateTo, 1);

  return html`
    <section class="card" id="travel">
      <h2>Getting there &amp; staying</h2>
      <form class="travel-form" id="travel-form">
        <label>From
          <select name="from">${homeCities.map((c) => html`<option value="${c.name}" ${c === city ? "selected" : ""}>${c.name}</option>`)}</select>
        </label>
        <label>People <input type="number" name="people" min="1" max="12" value="${people}" /></label>
        <button type="submit">Update</button>
      </form>

      <h3>Accommodation</h3>
      <p class="muted small">${date(e.dateFrom)} – ${date(checkOut)}, ${people} ${people === 1 ? "person" : "people"}. ${coords && info?.lat != null ? "Sorted by distance from the venue." : "Venue not set: searching around the city."}</p>
      <p>
        <a class="button" target="_blank" rel="noopener" href="${bookingUrl(place, coords, e.dateFrom, checkOut, people)}">Booking.com</a>
        <a class="button" target="_blank" rel="noopener" href="${airbnbUrl(place, coords, e.dateFrom, checkOut, people)}">Airbnb</a>
      </p>

      ${e.country === "Poland" ? trains(scraped, e, city, place) : flightsBlock(scraped, e, city, people, flights)}
    </section>`;
}

function trains(scraped: ScrapedEvent, e: { dateFrom: string; dateTo: string }, city: HomeCity, place: string): Raw {
  const s = scraped.station;
  if (!s) {
    return html`<h3>Train</h3><p><a class="button" target="_blank" rel="noopener" href="${googleTransitUrl(city.name, place)}">Google Maps: public transport</a></p>`;
  }
  const out = [addDays(e.dateFrom, -1), e.dateFrom];
  const back = [e.dateTo, addDays(e.dateTo, 1)];
  return html`
    <h3>Train</h3>
    <p>${city.name} → ${s.name}</p>
    <p>
      ${out.map((d) => html`<a class="button" target="_blank" rel="noopener" href="${koleoUrl(city.koleoSlug, s.slug, d)}">There: ${short(d)}</a> `)}
      <a class="button" target="_blank" rel="noopener" href="${koleoUrl(s.slug, city.koleoSlug, back[0], 12)}">Back: ${short(back[0])}</a>
      <a class="button" target="_blank" rel="noopener" href="${koleoUrl(s.slug, city.koleoSlug, back[1])}">Back: ${short(back[1])}</a>
    </p>
    <p class="muted small">Prices and connections on koleo.pl (PKP has no public price API).</p>`;
}

function flightsBlock(scraped: ScrapedEvent, e: { dateFrom: string; dateTo: string }, city: HomeCity, people: number, flights: Flights | undefined): Raw {
  const origins = flightOrigins(city);
  const destinations = scraped.airports ?? [];
  const google = googleFlightsUrl(origins, destinations.map((a) => a.iata), addDays(e.dateFrom, -1), e.dateTo);

  if (!destinations.length) {
    return html`<h3>Flights</h3><p class="muted">No nearby airports known${scraped.city ? "" : " (the event has no city)"}. An admin can add them.</p>`;
  }

  return html`
    <h3>Flights</h3>
    <p class="muted small">
      From ${origins.join(", ")} to ${destinations.map((a) => `${a.iata} (${a.name})`).join(", ")} ·
      out ${short(addDays(e.dateFrom, -1))}–${short(e.dateFrom)}, back ${short(e.dateTo)}–${short(addDays(e.dateTo, 1))}.
      Direct Ryanair and Wizz Air fares per person, cabin bag only${flights ? `, checked ${date(flights.fetchedOn)}` : ""}.
    </p>
    <p><a class="button" target="_blank" rel="noopener" href="${google}">Google Flights (all airlines, connections)</a></p>
    ${!flights
      ? html`<p class="muted">Fares not fetched yet: they refresh once a day.</p>`
      : !flights.combos.length
        ? html`<p class="muted">No direct Ryanair or Wizz Air flights in this window. Try Google Flights.</p>`
        : html`
          <h4>Cheapest return combinations</h4>
          <table class="flights">
            <thead><tr><th>Out</th><th>Back</th><th class="num">Per person</th>${people > 1 ? html`<th class="num">Total (${people})</th>` : ""}</tr></thead>
            <tbody>
              ${flights.combos.map((c) => html`
                <tr>
                  <td>${leg(c.out, people, false)}</td>
                  <td>${leg(c.back, people, false)}</td>
                  <td class="num"><strong>${money(c.perPerson, flights.currency)}</strong></td>
                  ${people > 1 ? html`<td class="num">${money(c.perPerson * people, flights.currency)}</td>` : ""}
                </tr>`)}
            </tbody>
          </table>
          <details>
            <summary>All flights found (${flights.out.length + flights.back.length})</summary>
            <div class="grid">
              <div><h4>Out</h4><ul class="legs">${flights.out.map((l) => html`<li>${leg(l, people, true)}</li>`)}</ul></div>
              <div><h4>Back</h4><ul class="legs">${flights.back.map((l) => html`<li>${leg(l, people, true)}</li>`)}</ul></div>
            </div>
          </details>`}`;
}

function leg(l: Leg, people: number, withPrice: boolean): Raw {
  const url = l.airline === "Ryanair" ? ryanairUrl(l.from, l.to, l.date, people) : wizzUrl(l.from, l.to, l.date, people);
  const times = l.durationMinutes != null
    ? html`<span class="muted">${l.times[0]}–${l.arrival} (${duration(l.durationMinutes)})</span>`
    : l.times.length ? html`<span class="muted">departs ${l.times.join(", ")}</span>` : "";
  return html`
    <a href="${url}" target="_blank" rel="noopener" class="leg">
      <span class="airline ${l.airline.toLowerCase()}">${l.airline === "Ryanair" ? "Ryanair" : "Wizz"}</span>
      ${l.from}→${l.to} · ${short(l.date)} ${times} <span class="muted">· direct</span>
      ${withPrice ? html`<strong> · ${money(l.price, l.currency)}</strong>` : ""}
    </a>`;
}

export function wireEvent(id: string): void {
  document.getElementById("travel-form")?.addEventListener("submit", (ev) => {
    ev.preventDefault();
    const data = new FormData(ev.target as HTMLFormElement);
    const params = new URLSearchParams({ from: String(data.get("from")), people: String(data.get("people")) });
    location.hash = `#/event/${id}?${params}`;
  });
}
