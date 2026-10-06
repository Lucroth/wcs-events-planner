import { describe, expect, it } from "vitest";
import { html, safeUrl } from "./html";
import { applyOverride, currentPass, isEurope, type Pass } from "./model";
import { addDays, bookingUrl, flightKey, homeCities, koleoUrl } from "./travel";

const pass = (kind: Pass["kind"], tier: string, until: string | null): Pass => ({ kind, tier, price: 100, currency: "EUR", until });

describe("currentPass", () => {
  const passes = [pass("Full", "Early", "2026-10-01"), pass("Full", "Regular", "2026-10-20"), pass("Full", "Late", null), pass("Party", "Early", "2026-10-10")];

  it("picks the nearest deadline not yet passed, then the door price", () => {
    expect(currentPass(passes, "Full", "2026-10-06")?.tier).toBe("Regular");
    expect(currentPass(passes, "Full", "2026-10-21")?.tier).toBe("Late");
    expect(currentPass(passes, "Party", "2026-10-11")).toBeNull();
  });
});

describe("html", () => {
  it("escapes interpolations but not nested html", () => {
    expect(html`<p>${"<script>"}</p>${html`<b>ok</b>`}`.value).toBe("<p>&lt;script&gt;</p><b>ok</b>");
  });

  it("lets only web links through", () => {
    expect(safeUrl("https://facebook.com/events/1")).toBe("https://facebook.com/events/1");
    expect(safeUrl("javascript:alert(1)")).toBeNull();
    expect(safeUrl("facebook.com")).toBeNull();
  });
});

describe("applyOverride", () => {
  it("lets the admin's corrections win and keeps the rest", () => {
    const e = { id: "1", name: "A", dateFrom: "2026-01-01", dateTo: "2026-01-03", city: null, country: "Poland", isWsdc: false };
    const v = applyOverride(e, { override: { city: "Kraków", isWsdc: true } });
    expect(v).toMatchObject({ name: "A", city: "Kraków", isWsdc: true, dateTo: "2026-01-03" });
  });
});

describe("travel links", () => {
  it("shifts dates across month ends", () => {
    expect(addDays("2026-10-31", 1)).toBe("2026-11-01");
    expect(addDays("2026-03-01", -1)).toBe("2026-02-28");
  });

  it("builds koleo's dd-MM-yyyy timetable path", () => {
    expect(koleoUrl("krakow", "warszawa", "2026-10-28")).toBe("https://koleo.pl/rozklad-pkp/krakow/warszawa/28-10-2026_06:00/all/all");
  });

  it("sorts Booking by distance when the venue is known", () => {
    const url = bookingUrl("Milan", { lat: 45.46, lng: 9.19 }, "2026-11-06", "2026-11-09", 3);
    expect(url).toContain("latitude=45.46");
    expect(url).toContain("order=distance_from_search");
    expect(url).toContain("group_adults=3");
  });

  it("keys flights the way the sync job writes them", () => {
    expect(flightKey(homeCities[0])).toBe("WAW-WMI");
    expect(flightKey(homeCities.find((c) => c.name === "Białystok")!)).toBe("WAW-WMI");
  });
});

describe("isEurope", () => {
  it("keeps European countries and drops the rest or the unknown", () => {
    expect(isEurope("Germany")).toBe(true);
    expect(isEurope("poland")).toBe(true);
    expect(isEurope("United States of America")).toBe(false);
    expect(isEurope(null)).toBe(false);
  });
});
