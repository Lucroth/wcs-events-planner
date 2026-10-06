import { getInfosForYear, getManualEvents, getYear } from "../firebase";
import { date, level, money, month, range, today } from "../format";
import { html, type Raw } from "../html";
import { applyOverride, currentPass, type Difficulty, type YearSummary } from "../model";

type Row = YearSummary["events"][number];

const firstYear = 2018;

export async function listPage(year: number, all: boolean): Promise<Raw> {
  const [summary, infos, manual] = await Promise.all([getYear(year), getInfosForYear(year), getManualEvents(year)]);

  const rows: Row[] = [
    ...(summary?.events ?? []),
    ...manual.map((e) => ({ id: e.id, name: e.name, dateFrom: e.dateFrom, dateTo: e.dateTo, city: e.city, country: e.country, isWsdc: e.isWsdc, chips: [] })),
  ]
    .map((r) => applyOverride(r, infos.get(r.id)))
    .filter((r) => all || r.isWsdc)
    .sort((a, b) => a.dateFrom.localeCompare(b.dateFrom));

  const now = today();
  const thisYear = new Date().getFullYear();
  const years = Array.from({ length: thisYear + 2 - firstYear + 1 }, (_, i) => thisYear + 2 - i);

  const byMonth = new Map<number, Row[]>();
  for (const r of rows) {
    const m = Number(r.dateFrom.slice(5, 7));
    byMonth.set(m, [...(byMonth.get(m) ?? []), r]);
  }

  return html`
    <h1>WSDC events ${year}</h1>
    <div class="filters">
      <nav class="years">
        ${years.map((y) => html`<a href="#/year/${y}${all ? "?all" : ""}" class="${y === year ? "chip on" : "chip"}">${y}</a>`)}
      </nav>
      <label><input type="checkbox" id="show-all" ${all ? "checked" : ""} /> include events without WSDC points</label>
    </div>
    ${rows.length === 0
      ? html`<p class="muted">No events in ${year}.</p>`
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
                      ${chips(r.chips)}
                    </a>
                  </li>`;
              })}
            </ul>`,
        )}
    <p class="muted small">Chips: how hard each division's field was (green easy, amber medium, red hard), from the latest edition with results. Updated ${date(now)}.</p>`;
}

function chips(list: { division: string; level: Difficulty | null }[]): Raw {
  if (!list.length) return html``;
  return html`<span class="chips">${list.map(
    (c) => html`<span class="chip diff-${c.level ? level(c.level) : "none"}" title="${c.level ? level(c.level) : "not enough data"}">${c.division}</span>`,
  )}</span>`;
}

export function wireList(year: number): void {
  document.getElementById("show-all")?.addEventListener("change", (e) => {
    location.hash = `#/year/${year}${(e.target as HTMLInputElement).checked ? "?all" : ""}`;
  });
}
