import { getInfosForYear, getManualEvents, getYear } from "../firebase";
import { date, level, money, month, range, today } from "../format";
import { html, type Raw } from "../html";
import { applyOverride, currentPass, isEurope, matchesLevel, type Difficulty, type YearSummary } from "../model";

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

/** What the list is narrowed to; kept in the URL so a filtered view can be shared. */
export interface ListFilter {
  all: boolean;
  division: string | null;
  levels: Difficulty[];
}

export function readFilter(params: URLSearchParams): ListFilter {
  const division = params.get("div");
  return {
    all: params.has("all"),
    division: divisions.some(([d]) => d === division) ? division : null,
    levels: (params.get("level") ?? "").split(",").filter((l): l is Difficulty => levels.includes(l as Difficulty)),
  };
}

function query(f: ListFilter): string {
  const parts: string[] = [];
  if (f.all) parts.push("all");
  if (f.division) parts.push(`div=${f.division}`);
  if (f.levels.length) parts.push(`level=${f.levels.join(",")}`);
  return parts.length ? `?${parts.join("&")}` : "";
}

export async function listPage(year: number, filter: ListFilter): Promise<Raw> {
  const [summary, infos, manual] = await Promise.all([getYear(year), getInfosForYear(year), getManualEvents(year)]);

  const rows: Row[] = [
    ...(summary?.events ?? []),
    ...manual.map((e) => ({ id: e.id, name: e.name, dateFrom: e.dateFrom, dateTo: e.dateTo, city: e.city, country: e.country, isWsdc: e.isWsdc, chips: [] })),
  ]
    .map((r) => applyOverride(r, infos.get(r.id)))
    .filter((r) => isEurope(r.country))
    .filter((r) => filter.all || r.isWsdc)
    .filter((r) => matchesLevel(r.chips, filter.division, filter.levels))
    .sort((a, b) => a.dateFrom.localeCompare(b.dateFrom));

  const now = today();
  const thisYear = new Date().getFullYear();
  const years = Array.from({ length: thisYear + 2 - firstYear + 1 }, (_, i) => thisYear + 2 - i);
  const filtered = filter.division !== null || filter.levels.length > 0;

  const byMonth = new Map<number, Row[]>();
  for (const r of rows) {
    const m = Number(r.dateFrom.slice(5, 7));
    byMonth.set(m, [...(byMonth.get(m) ?? []), r]);
  }

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
      <label><input type="checkbox" name="all" ${filter.all ? "checked" : ""} /> include events without WSDC points</label>
      ${filtered ? html`<a href="#/year/${year}${filter.all ? "?all" : ""}">clear filter</a>` : ""}
    </form>
    ${filtered ? html`<p class="muted small">${filterNote(filter)} Events with no results yet, for this or an earlier edition, have no level and are hidden.</p>` : ""}
    ${rows.length === 0
      ? html`<p class="muted">No ${filtered ? "matching " : ""}European events in ${year}.</p>`
      : [...byMonth].map(
          ([m, list]) => html`
            <h2 class="month">${month(m)}</h2>
            <ul class="events">
              ${list.map((r) => {
                const info = infos.get(r.id);
                const full = currentPass(info?.passes, "Full", now);
                const party = currentPass(info?.passes, "Party", now);
                return html`
                  <li class="${r.dateTo < now ? "past" : ""}">
                    <a href="#/event/${r.id}" class="event-card">
                      <span class="dates">${range(r.dateFrom, r.dateTo)}</span>
                      <span class="name">${r.name} ${r.country === "Poland" ? html`<span class="tag pl">PL</span>` : ""}</span>
                      <span class="where muted">${[r.city, r.country].filter(Boolean).join(", ")}</span>
                      <span class="price">
                        ${full ? html`<span>Full ${money(full.price, full.currency)}</span>` : ""}
                        ${party ? html`<span>Party ${money(party.price, party.currency)}</span>` : ""}
                      </span>
                      ${chips(r.chips, filter.division)}
                    </a>
                  </li>`;
              })}
            </ul>`,
        )}
    <p class="muted small">Chips: how hard each division's field was (green easy, amber medium, red hard), ranked by the strongest quarter of the field at the latest edition with results. Updated ${date(now)}.</p>`;
}

function filterNote(f: ListFilter): string {
  const division = divisions.find(([d]) => d === f.division)?.[1];
  const lv = f.levels.map(level).join(" or ");
  if (division && lv) return `Showing events where ${division} is ${lv}.`;
  if (division) return `Showing events with a ${division} division.`;
  return `Showing events with at least one ${lv} division.`;
}

function chips(list: { division: string; level: Difficulty | null }[], mine: string | null): Raw {
  if (!list.length) return html``;
  return html`<span class="chips">${list.map(
    (c) => html`<span class="chip diff-${c.level ? level(c.level) : "none"} ${c.division === mine ? "mine" : ""}" title="${c.level ? level(c.level) : "not enough data"}">${c.division}</span>`,
  )}</span>`;
}

export function wireList(year: number): void {
  const form = document.getElementById("list-filter") as HTMLFormElement | null;
  form?.addEventListener("change", () => {
    const data = new FormData(form);
    const filter: ListFilter = {
      all: data.has("all"),
      division: String(data.get("div") ?? "") || null,
      levels: data.getAll("level").map(String) as Difficulty[],
    };
    location.hash = `#/year/${year}${query(filter)}`;
  });
}
