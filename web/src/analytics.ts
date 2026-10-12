/** The site code from yourcode.goatcounter.com; while empty nothing is loaded or sent. */
const code = "";

interface GoatCounter {
  count(vars: { path: string; title?: string }): void;
}

let loaded = false;
let last = "";

/** Counts the page the hash route shows; GoatCounter's own count only sees the address without the hash. */
export function countPage(): void {
  if (!code || location.hash.startsWith("#/admin")) return;
  const path = location.hash.replace(/^#/, "").split("?")[0] || "/";
  if (path === last) return;
  const goat = (window as unknown as { goatcounter?: GoatCounter }).goatcounter;
  if (goat?.count) {
    last = path;
    goat.count({ path });
    return;
  }
  if (loaded) return;
  loaded = true;
  const script = document.createElement("script");
  script.async = true;
  script.src = "https://gc.zgo.at/count.js";
  script.dataset.goatcounter = `https://${code}.goatcounter.com/count`;
  script.dataset.goatcounterSettings = JSON.stringify({ no_onload: true });
  script.addEventListener("load", () => countPage());
  document.head.append(script);
}
