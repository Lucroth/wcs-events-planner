import { getAllFlights, getEvent, getInfo, getScheduleImage, getTrains, getYear } from "../firebase";
import { isImageDataUrl } from "../image";
import { date, duration, level, localDateTime, money, range, short, today } from "../format";
import { html, safeUrl, type Raw } from "../html";
import { liveCard, mayBeLive, watchLive } from "../live";
import { applyOverride, currentPass, tierFor, tiers, type Flights, type Info, type Leg, type Pass, type ScrapedEvent, type TrainLeg, type Trains } from "../model";
import {
  addDays,
  airbnbUrl,
  bookingUrl,
  findCity,
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
  const [flights, trains, eventImage, compImage] = await Promise.all([
    upcoming && e.country !== "Poland" ? getAllFlights(id).then(mergeFlights) : undefined,
    upcoming && e.country === "Poland" ? getTrains(id, city.koleoSlug) : undefined,
    info?.eventScheduleImage ? getScheduleImage(id, "event") : undefined,
    info?.compScheduleImage ? getScheduleImage(id, "comp") : undefined,
  ]);

  return html`
    <nav class="back editions">
      <a href="#/year/${e.dateFrom.slice(0, 4)}">← ${e.dateFrom.slice(0, 4)} events</a>
      <span class="buttons">
        ${scraped?.previous ? html`<a class="button small" href="#/event/${scraped.previous.id}" title="${scraped.previous.name}">‹ ${scraped.previous.dateFrom?.slice(0, 4) ?? "Previous"} edition</a>` : ""}
        ${scraped?.next ? html`<a class="button small" href="#/event/${scraped.next.id}" title="${scraped.next.name}">${scraped.next.dateFrom?.slice(0, 4) ?? "Next"} edition ›</a>` : ""}
      </span>
    </nav>
    ${params.has("next") ? await nextEdition(id, Number(params.get("next"))) : ""}
    <h1>${e.name}</h1>
    <p class="lead">
      ${range(e.dateFrom, e.dateTo)} ${e.dateFrom.slice(0, 4)} · ${[e.city, e.country].filter(Boolean).join(", ")}
      ${info?.venueName ? html` · <a href="${mapsUrl(info.venueAddress || info.venueName)}" target="_blank" rel="noopener">${info.venueName}</a>` : ""}
      ${e.isWsdc ? html`<span class="tag">WSDC</span>` : ""}
      ${admin ? html`<a class="button small" href="#/admin/event/${id}">Edit</a>` : ""}
    </p>
    ${info?.autofill ? html`<p class="notice small">Prices and details were copied automatically from <a href="${safeUrl(info.autofill.source) ?? "#"}" target="_blank" rel="noopener">the event's website</a> on ${date(info.autofill.on)} and not yet reviewed; check there before buying.</p>` : ""}
    <section class="links">${links(scraped, info)}</section>
    ${(mayBeLive(e.dateFrom, e.dateTo, now) || params.has("live")) && /^\d+$/.test(id) ? html`<section class="card live-card" id="live" data-id="${id}" hidden><h2><span class="tag live">LIVE</span> Competitions</h2><div id="live-body"></div></section>` : ""}
    <div class="grid">
      ${passesCard(info, now)}
      <section class="card">
        <h2>Staff</h2>
        ${info?.staff?.length ? html`<ul class="staff">${info.staff.map((s) => html`<li>${s}</li>`)}</ul>` : html`<p class="muted">No staff list yet.</p>`}
      </section>
    </div>
    <div class="grid">
      ${schedule("Event schedule", info?.eventSchedule, eventImage)}
      ${schedule("Competition schedule", info?.compSchedule, compImage)}
    </div>
    ${strengthCard(scraped)}
    ${resultsCard(scraped)}
    ${upcoming ? travelCard(scraped, e, info, city, people, flights, trains) : ""}`;
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

function schedule(title: string, text: string | null | undefined, image: string | undefined): Raw {
  const url = safeUrl(text);
  const img = isImageDataUrl(image) ? image : null;
  const body = !text?.trim()
    ? img ? "" : html`<p class="muted">Not published yet.</p>`
    : url ? html`<p><a href="${url}" target="_blank" rel="noopener">See the schedule</a></p>` : html`<pre class="schedule">${text}</pre>`;
  return html`
    <section class="card">
      <h2>${title}</h2>
      ${body}
      ${img ? html`<img class="schedule-image zoomable" src="${img}" alt="${title}" title="Click to enlarge" />` : ""}
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
      <p class="muted small">WSDC points competitors held in the division when they danced. <strong>Top 25%</strong> is the average of the strongest quarter of the field, roughly who you have to beat to make the final; the level is ranked by it ("hard" = top third of all events in that division). <strong>Top 25% median</strong> is the middle of that quarter, less swayed by one very experienced dancer. <strong>Avg (all)</strong> covers everyone, so it mostly shows how many entrants have no points yet.</p>
      <table>
        <thead><tr><th>Division</th><th></th><th class="num">Dancers</th><th>Tier</th><th class="num">Top 25% avg</th><th class="num">Top 25% median</th><th class="num">Avg (all)</th><th>Level</th></tr></thead>
        <tbody>
          ${e.strengths.map((s) => html`
            <tr>
              <td>${s.division}</td>
              <td>${s.role === "Leader" ? "Leaders" : "Followers"}</td>
              <td class="num">${s.fieldSize}</td>
              <td>${tierCell(s.fieldSize)}</td>
              <td class="num" ${s.europeTopQuartileAverage != null ? html`title="Average across European events: ${s.europeTopQuartileAverage.toFixed(1)}"` : ""}><strong>${s.topQuartileAverage != null ? s.topQuartileAverage.toFixed(1) : "—"}</strong></td>
              <td class="num">${s.medianPoints}</td>
              <td class="num">${s.averagePoints.toFixed(1)}</td>
              <td>${s.difficulty ? html`<span class="chip diff-${level(s.difficulty)}">${level(s.difficulty)}</span>` : ""}</td>
            </tr>`)}
        </tbody>
      </table>
      ${tiersTable()}
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
            <ol>${r.places.map((p) => html`<li value="${p.position}">${p.dancers?.length ? couple(p.dancers) : p.names}</li>`)}</ol>
          </div>`)}
      </div>
    </section>`;
}

function travelCard(scraped: ScrapedEvent, e: { dateFrom: string; dateTo: string; city: string | null; country: string | null }, info: Info | undefined, city: HomeCity, people: number, flights: Flights | undefined, trains: Trains | undefined): Raw {
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
      <p class="muted small">${date(e.dateFrom)} – ${date(checkOut)}, ${people} ${people === 1 ? "person" : "people"}. ${info?.lat != null || (info?.venueAddress && scraped.coords) ? "Sorted by distance from the venue." : info?.venueAddress ? "Venue address could not be placed on a map yet: searching around the city." : "Venue not set: searching around the city."}</p>
      <p class="buttons">
        <a class="button" target="_blank" rel="noopener" href="${bookingUrl(place, coords, e.dateFrom, checkOut, people)}">Booking.com</a>
        <a class="button" target="_blank" rel="noopener" href="${airbnbUrl(place, coords, e.dateFrom, checkOut, people)}">Airbnb</a>
      </p>

      ${e.country === "Poland" ? trainsBlock(scraped, e, city, place, people, trains) : flightsBlock(scraped, e, city, people, flights)}
    </section>`;
}

function trainsBlock(scraped: ScrapedEvent, e: { dateFrom: string; dateTo: string }, city: HomeCity, place: string, people: number, fares: Trains | undefined): Raw {
  const s = scraped.station;
  if (!s) {
    return html`<h3>Train</h3><p class="buttons"><a class="button" target="_blank" rel="noopener" href="${googleTransitUrl(city.name, place)}">Google Maps: public transport</a></p>`;
  }
  // Station names start with the city's ("Warszawa Centralna"): nothing to book from home to home.
  if (s.name.toLowerCase().startsWith(city.name.toLowerCase())) {
    return html``;
  }
  // Any day of the event works for a trip there or back: some come for the weekend only.
  const out = days(addDays(e.dateFrom, -1), addDays(e.dateTo, -1));
  const back = days(addDays(e.dateFrom, 1), addDays(e.dateTo, 1));
  return html`
    <h3>Train</h3>
    <p>${city.name} → ${s.name}</p>
    <p class="buttons">
      ${out.map((d) => html`<a class="button" target="_blank" rel="noopener" href="${koleoUrl(city.koleoSlug, s.slug, d)}">There: ${short(d)}</a>`)}
    </p>
    <p class="buttons">
      ${back.map((d) => html`<a class="button" target="_blank" rel="noopener" href="${koleoUrl(s.slug, city.koleoSlug, d, d === e.dateTo ? 12 : 6)}">Back: ${short(d)}</a>`)}
    </p>
    ${fares ? trainFares(fares, people, city, s.slug) : html`<p class="muted small">Fares appear here about a month before the event, when PKP Intercity starts selling. Until then, check koleo.pl.</p>`}`;
}

/** One view over every home city's fares: combos and legs pooled, duplicates dropped, cheapest first. */
function mergeFlights(all: Flights[]): Flights | undefined {
  if (!all.length) return undefined;
  const legKey = (l: Leg) => `${l.airline}|${l.from}|${l.to}|${l.date}|${l.times.join(",")}`;
  const unique = <T,>(items: T[], key: (t: T) => string) => [...new Map(items.map((i) => [key(i), i])).values()];
  return {
    origins: unique(all.flatMap((f) => f.origins), (o) => o),
    destinations: unique(all.flatMap((f) => f.destinations), (d) => d),
    currency: all[0].currency,
    combos: unique(all.flatMap((f) => f.combos), (c) => `${legKey(c.out)}>${legKey(c.back)}`).sort((a, b) => a.perPerson - b.perPerson).slice(0, 20),
    out: unique(all.flatMap((f) => f.out), legKey).sort((a, b) => a.price - b.price),
    back: unique(all.flatMap((f) => f.back), legKey).sort((a, b) => a.price - b.price),
    fetchedOn: all.map((f) => f.fetchedOn).sort().at(-1)!,
  };
}

function flightsBlock(scraped: ScrapedEvent, e: { dateFrom: string; dateTo: string; city: string | null }, city: HomeCity, people: number, flights: Flights | undefined): Raw {
  const home = new Set(flightOrigins(city));
  const origins = flights?.origins.length ? flights.origins : [...home];
  const destinations = scraped.airports ?? [];
  const google = googleFlightsUrl(city.airports.length ? city.name : "Warsaw", e.city ?? destinations[0]?.name ?? "", addDays(e.dateFrom, -1), e.dateTo);

  if (!destinations.length) {
    return html`<h3>Flights</h3><p class="muted">No nearby airports known${scraped.city ? "" : " (the event has no city)"}. An admin can add them.</p>`;
  }

  return html`
    <h3>Flights</h3>
    <p class="muted small">
      From ${origins.join(", ")} to ${destinations.map((a) => `${a.iata} (${a.name})`).join(", ")} ·
      out ${short(addDays(e.dateFrom, -1))}–${short(e.dateFrom)}, back ${short(e.dateTo)}–${short(addDays(e.dateTo, 1))}.
      Direct Ryanair and Wizz Air fares per person, cabin bag only${flights ? `, checked ${date(flights.fetchedOn)}` : ""}. Flights from ${city.name}${city.airports.length ? "" : " (Warsaw)"} are marked.
    </p>
    <p class="buttons"><a class="button" target="_blank" rel="noopener" href="${google}">Google Flights (all airlines, connections)</a></p>
    ${!flights
      ? html`<p class="muted">Fares not fetched yet: they refresh once a day.</p>`
      : !flights.combos.length
        ? html`<p class="muted">No direct Ryanair or Wizz Air flights in this window. Try Google Flights.</p>`
        : html`
          <h4>Cheapest return combinations, any Polish airport</h4>
          <table class="flights">
            <thead><tr><th>Out</th><th>Back</th><th class="num">Per person</th>${people > 1 ? html`<th class="num">Total (${people})</th>` : ""}</tr></thead>
            <tbody>
              ${flights.combos.map((c) => html`
                <tr class="${home.has(c.out.from) ? "mine" : ""}">
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

function trainFares(t: Trains, people: number, city: HomeCity, stationSlug: string): Raw {
  const row = (l: TrainLeg, from: string, to: string) => html`
    <li><a class="leg" href="${koleoUrl(from, to, l.date, Number(l.departure.slice(0, 2)))}" target="_blank" rel="noopener">
      ${short(l.date)} ${l.departure}–${l.arrival} <span class="muted">(${duration(l.durationMinutes)}, ${l.changes ? `${l.changes} change${l.changes > 1 ? "s" : ""}` : "direct"})</span>
      <strong> · ${money(l.price, t.currency)}</strong>
    </a></li>`;
  return html`
    ${t.cheapest != null
      ? html`<p>Cheapest return: <strong>${money(t.cheapest, t.currency)}</strong> per person${people > 1 ? html`, <strong>${money(t.cheapest * people, t.currency)}</strong> for ${people}` : ""}.</p>`
      : ""}
    <div class="grid">
      <div><h4>There</h4>${t.out.length ? html`<ul class="legs">${t.out.map((l) => row(l, city.koleoSlug, stationSlug))}</ul>` : html`<p class="muted">Not on sale yet.</p>`}</div>
      <div><h4>Back</h4>${t.back.length ? html`<ul class="legs">${t.back.map((l) => row(l, stationSlug, city.koleoSlug))}</ul>` : html`<p class="muted">Not on sale yet.</p>`}</div>
    </div>
    <p class="muted small">Standard one-way fares per adult from koleo.pl, checked ${date(t.fetchedOn)}; discounts (students, ISIC, Big Family Card) not included.</p>`;
}

function tierCell(fieldSize: number): Raw {
  const t = tierFor(fieldSize);
  return t
    ? html`<span title="1st–5th: ${t.points.join(" / ")} points${t.extra ? `; ${t.extra} more in the final` : ""}">Tier ${t.tier}</span>`
    : html`<span class="muted" title="Fewer than 5 competitors: no points awarded">—</span>`;
}

function tiersTable(): Raw {
  return html`
    <details>
      <summary>WSDC tiers and points (Registry Event Rules, Chart 5)</summary>
      <table>
        <thead><tr><th>Tier</th><th class="num">Competitors per role</th><th class="num">1st</th><th class="num">2nd</th><th class="num">3rd</th><th class="num">4th</th><th class="num">5th</th><th>Other finalists</th></tr></thead>
        <tbody>
          ${tiers.map((t) => html`<tr><td>Tier ${t.tier}</td><td class="num">${t.max === Infinity ? `${t.min}+` : `${t.min}–${t.max}`}</td>${t.points.map((p) => html`<td class="num">${p}</td>`)}<td>${t.extra ? `${t.extra}` : "0"}</td></tr>`)}
        </tbody>
      </table>
      <p class="muted small">Tier here is estimated from the dancers in the largest round of each division, the closest the published results come to the official unique-competitor count.</p>
    </details>`;
}

export function wireEvent(id: string): void {
  const card = document.getElementById("live");
  if (card) {
    watchLive(id, (live) => {
      card.hidden = !live?.rounds.length;
      document.getElementById("live-body")!.innerHTML = liveCard(live, id).value;
    });
  }

  document.getElementById("travel-form")?.addEventListener("submit", (ev) => {
    ev.preventDefault();
    const data = new FormData(ev.target as HTMLFormElement);
    const params = new URLSearchParams({ from: String(data.get("from")), people: String(data.get("people")) });
    location.hash = `#/event/${id}?${params}`;
  });
}

/** Each dancer's scoring.dance registry page, for those with a WSDC id (given with their first points). */
function couple(dancers: { name: string; wscid: number | null }[]): Raw {
  return html`${dancers.map((d, i) => html`${i ? " & " : ""}${d.wscid ? html`<a href="https://scoring.dance/enUS/wsdc/registry/${d.wscid}.html" target="_blank" rel="noopener">${d.name}</a>` : d.name}`)}`;
}

/** Every day from `from` to `to` inclusive, at most a week. */
function days(from: string, to: string): string[] {
  const all: string[] = [];
  for (let d = from; d <= to && all.length < 7; d = addDays(d, 1)) all.push(d);
  return all;
}

/** Opened from an expected card: what is known of the next edition, above the latest one's page. */
async function nextEdition(id: string, year: number): Promise<Raw> {
  const row = (await getYear(year))?.events.find((r) => r.id === id && r.expected);
  if (!row) return html``;
  const a = row.announced;
  const site = safeUrl(a?.websiteUrl);
  return html`
    <section class="notice next-edition">
      <strong>Next edition${a ? `: ${range(row.dateFrom, row.dateTo)} ${row.dateTo.slice(0, 4)}` : ` expected around ${new Date(`${row.dateFrom}T00:00:00Z`).toLocaleDateString("en-GB", { month: "long", year: "numeric", timeZone: "UTC" })}`}</strong>
      ${a ? html`· ${[a.venue, row.city, row.country].filter(Boolean).join(", ")}` : ""}
      ${site ? html`· <a href="${site}" target="_blank" rel="noopener">event website</a>` : ""}
      <br /><span class="muted small">${a ? "Announced by the organiser, not on scoring.dance yet." : "Dates not announced yet."} Below: the latest edition, for passes, staff and how hard the competitions were.</span>
    </section>`;
}
