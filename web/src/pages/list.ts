import { getCheapestTravel, getInfosForYear, getManualEvents, getYear, plnRates } from "../firebase";
import { date, level, money, month, range, today } from "../format";
import { html, type Raw } from "../html";
import { applyOverride, currentPass, isEurope, levelScore, matchesLevel, type Chip, type Difficulty, type Info, type YearSummary } from "../model";
import { findCity, flightKey, homeCities } from "../travel";

type Row = YearSummary["events"][number];

const firstYear = 2018;

const divisions: [string, string][] = [
  ["NEW", "Newcomer"],
  ["NOV", "Novice"],
  ["INT", "Intermediate"],
  ["ADV", "Advanced"],
  ["ALS", "All-Stars"],
  ["CHMP", "Champions"],
];

const levels: Difficulty[] = ["Easy", "Medium", "Hard"];

const sorts = [
  ["date", "by date"],
  ["hard", "hardest first"],
  ["easy", "easiest first"],
  ["cost", "cheapest trip first"],
] as const;

export type SortBy = (typeof sorts)[number][0];

/** What the list is narrowed to and how it is ordered; kept in the URL so a view can be shared. */
export interface ListFilter {
  all: boolean;
  /** Hide events that have already ended. */
  upcoming: boolean;
  division: string | null;
  levels: Difficulty[];
  sort: SortBy;
  /** Home city for the cost sort. */
  from: string | null;
}

export function readFilter(params: URLSearchParams): ListFilter {
  const division = params.get("div");
  return {
    all: params.has("all"),
    upcoming: params.has("upcoming"),
    division: divisions.some(([d]) => d === division) ? division : null,
    levels: (params.get("level") ?? "").split(",").filter((l): l is Difficulty => levels.includes(l as Difficulty)),
    sort: sorts.find(([s]) => s === params.get("sort"))?.[0] ?? "date",
    from: params.get("from"),
  };
}

function query(f: ListFilter): string {
  const parts: string[] = [];
  if (f.all) parts.push("all");
  if (f.upcoming) parts.push("upcoming");
  if (f.division) parts.push(`div=${f.division}`);
  if (f.levels.length) parts.push(`level=${f.levels.join(",")}`);
  if (f.sort !== "date") parts.push(`sort=${f.sort}`);
  if (f.sort === "cost" && f.from) parts.push(`from=${encodeURIComponent(f.from)}`);
  return parts.length ? `?${parts.join("&")}` : "";
}

interface Cost {
  total: number;
  travel: number;
  /** Full pass in PLN; null when no price is known. */
  pass: number | null;
}

export async function listPage(year: number, filter: ListFilter): Promise<Raw> {
  const now = today();
  const [summary, infos, manual] = await Promise.all([getYear(year), getInfosForYear(year), getManualEvents(year)]);

  const rows: Row[] = [
    ...(summary?.events ?? []),
    ...manual.map((e) => ({ id: e.id, name: e.name, dateFrom: e.dateFrom, dateTo: e.dateTo, city: e.city, country: e.country, isWsdc: e.isWsdc, chips: [] })),
  ]
    .map((r) => applyOverride(r, infos.get(r.id)))
    .filter((r) => isEurope(r.country))
    .filter((r) => filter.all || r.isWsdc)
    .filter((r) => !filter.upcoming || r.dateTo >= now)
    .filter((r) => matchesLevel(r.chips, filter.division, filter.levels))
    .sort((a, b) => a.dateFrom.localeCompare(b.dateFrom));

  const home = findCity(filter.from);
  const costs = filter.sort === "cost" ? await tripCosts(rows, infos, filter.from, now) : new Map<string, Cost>();
  const thisYear = new Date().getFullYear();
  const years = Array.from({ length: thisYear + 2 - firstYear + 1 }, (_, i) => thisYear + 2 - i);
  const filtered = filter.division !== null || filter.levels.length > 0;

  const byMonth = new Map<number, Row[]>();
  for (const r of rows) {
    const m = Number(r.dateFrom.slice(5, 7));
    byMonth.set(m, [...(byMonth.get(m) ?? []), r]);
  }

  const list =
    rows.length === 0
      ? html`<p class="muted">No ${filtered ? "matching " : ""}European events in ${year}.</p>`
      : filter.sort === "date"
        ? [...byMonth].map(
            ([m, monthRows]) => html`
              <h2 class="month">${month(m)}</h2>
              <ul class="events">${monthRows.map((r) => card(r, infos.get(r.id), now, filter.division, undefined))}</ul>`,
          )
        : html`<ul class="events">${sortRows(rows, filter, costs).map((r) => card(r, infos.get(r.id), now, filter.division, costs.get(r.id)))}</ul>`;

  return html`
    <h1>WSDC events in Europe ${year}</h1>
    <div class="filters">
      <nav class="years">
        ${years.map((y) => html`<a href="#/year/${y}${query(filter)}" class="${y === year ? "chip on" : "chip"}">${y}</a>`)}
      </nav>
    </div>
    <form class="filters" id="list-filter">
      <label>My division
        <select name="div">
          <option value="">any</option>
          ${divisions.map(([d, name]) => html`<option value="${d}" ${d === filter.division ? "selected" : ""}>${name}</option>`)}
        </select>
      </label>
      <fieldset class="levels">
        <legend>Level</legend>
        ${levels.map((l) => html`<label class="chip diff-${level(l)} ${filter.levels.includes(l) ? "on-level" : ""}"><input type="checkbox" name="level" value="${l}" ${filter.levels.includes(l) ? "checked" : ""} /> ${level(l)}</label>`)}
      </fieldset>
      <label>Sort
        <select name="sort">${sorts.map(([v, label]) => html`<option value="${v}" ${filter.sort === v ? "selected" : ""}>${label}</option>`)}</select>
      </label>
      ${filter.sort === "cost"
        ? html`<label>From
            <select name="from">${homeCities.map((c) => html`<option value="${c.name}" ${c === home ? "selected" : ""}>${c.name}</option>`)}</select>
          </label>`
        : ""}
      <label><input type="checkbox" name="upcoming" ${filter.upcoming ? "checked" : ""} /> hide finished events</label>
      <label><input type="checkbox" name="all" ${filter.all ? "checked" : ""} /> include events without WSDC points</label>
      ${filtered ? html`<a href="#/year/${year}${query({ ...filter, division: null, levels: [] })}">clear filter</a>` : ""}
    </form>
    ${filtered ? html`<p class="muted small">${filterNote(filter)} Events with no results yet, for this or an earlier edition, have no level and are hidden.</p>` : ""}
    ${filter.sort === "hard" || filter.sort === "easy"
      ? html`<p class="muted small">Ordered by ${filter.division ? "your division's" : "the average"} level, using the strongest quarter of the field. Events without a level come last.</p>`
      : ""}
    ${filter.sort === "cost"
      ? html`<p class="muted small">Trip cost per person from ${home.name}: the cheapest direct return flight or train, plus the full pass on sale today, converted to PLN at today's NBP rate (* = pass price unknown). Accommodation is not included: Booking and Airbnb publish no prices. Events without known fares (too far ahead, or no airport known) come last.</p>`
      : ""}
    ${list}
    <p class="muted small">Chips: how hard each division's field was (green easy, amber medium, red hard), ranked by the strongest quarter of the field at the latest edition with results. Updated ${date(now)}.</p>`;
}

/** Per person, in PLN: cheapest return travel from home plus the full pass on sale today. Only events with a known fare get one. */
async function tripCosts(rows: Row[], infos: Map<string, Info>, from: string | null, now: string): Promise<Map<string, Cost>> {
  const city = findCity(from);
  const [travel, rates] = await Promise.all([getCheapestTravel(flightKey(city), city.koleoSlug), plnRates()]);
  const costs = new Map<string, Cost>();

  for (const r of rows) {
    // An event in the reader's own city costs nothing to reach.
    const local = r.country === "Poland" && !!r.city && r.city.toLowerCase().startsWith(city.name.toLowerCase());
    const fare = local ? 0 : travel.get(r.id);
    if (fare === undefined) continue;

    const pass = currentPass(infos.get(r.id)?.passes, "Full", now);
    const rate = pass ? rates.get(pass.currency) : undefined;
    const passPln = pass && rate ? pass.price * rate : null;
    costs.set(r.id, { total: fare + (passPln ?? 0), travel: fare, pass: passPln });
  }

  return costs;
}

function sortRows(rows: Row[], f: ListFilter, costs: Map<string, Cost>): Row[] {
  const value = (r: Row): number | null => (f.sort === "cost" ? costs.get(r.id)?.total ?? null : levelScore(r.chips, f.division));
  const dir = f.sort === "hard" ? -1 : 1;

  return [...rows].sort((a, b) => {
    const va = value(a);
    const vb = value(b);
    if (va === null || vb === null) return va === vb ? a.dateFrom.localeCompare(b.dateFrom) : va === null ? 1 : -1;
    return (va - vb) * dir || a.dateFrom.localeCompare(b.dateFrom);
  });
}

function card(r: Row, info: Info | undefined, now: string, mine: string | null, cost: Cost | undefined): Raw {
  const full = currentPass(info?.passes, "Full", now);
  const party = currentPass(info?.passes, "Party", now);
  const price = cost
    ? html`<span title="travel ${money(Math.round(cost.travel), "PLN")}${cost.pass != null ? ` + pass ${money(Math.round(cost.pass), "PLN")}` : ", pass price unknown"}">≈ <strong>${money(Math.round(cost.total), "PLN")}</strong>${cost.pass == null ? "*" : ""}</span>`
    : html`${full ? html`<span>Full ${money(full.price, full.currency)}</span>` : ""}${party ? html`<span>Party ${money(party.price, party.currency)}</span>` : ""}`;

  return html`
    <li class="${r.dateTo < now ? "past" : ""}">
      <a href="#/event/${r.id}" class="event-card">
        <span class="dates">${range(r.dateFrom, r.dateTo)}</span>
        <span class="name">${r.name} ${r.country === "Poland" ? html`<span class="tag pl">PL</span>` : ""}</span>
        <span class="where muted">${[r.city, r.country].filter(Boolean).join(", ")}</span>
        <span class="price">${price}</span>
        ${chips(r.chips, mine)}
      </a>
    </li>`;
}

function filterNote(f: ListFilter): string {
  const division = divisions.find(([d]) => d === f.division)?.[1];
  const lv = f.levels.map(level).join(" or ");
  if (division && lv) return `Showing events where ${division} is ${lv}.`;
  if (division) return `Showing events with a ${division} division.`;
  return `Showing events with at least one ${lv} division.`;
}

function chips(list: Chip[], mine: string | null): Raw {
  if (!list.length) return html``;
  return html`<span class="chips">${list.map(
    (c) => html`<span class="chip diff-${c.level ? level(c.level) : "none"} ${c.division === mine ? "mine" : ""}" title="${c.level ? level(c.level) : "not enough data"}${c.top != null ? `, top 25% avg ${c.top} pts` : ""}">${c.division}</span>`,
  )}</span>`;
}

export function wireList(year: number): void {
  const form = document.getElementById("list-filter") as HTMLFormElement | null;
  form?.addEventListener("change", () => {
    const data = new FormData(form);
    const filter: ListFilter = {
      all: data.has("all"),
      upcoming: data.has("upcoming"),
      division: String(data.get("div") ?? "") || null,
      levels: data.getAll("level").map(String) as Difficulty[],
      sort: (String(data.get("sort") ?? "date") as SortBy) || "date",
      from: data.has("from") ? String(data.get("from")) : null,
    };
    location.hash = `#/year/${year}${query(filter)}`;
  });
}
