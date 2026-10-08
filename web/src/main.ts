import "./style.css";
import { onAuthStateChanged, type User } from "firebase/auth";
import { auth, isAdmin } from "./firebase";
import { signIn } from "./me";
import { html, type Raw } from "./html";
import { stopListening } from "./live";
import { dashboardPage, editPage, loginPage, logout, wireEdit, wireLogin } from "./pages/admin";
import { eventPage, wireEvent } from "./pages/event";
import { listPage, readFilter, wireList } from "./pages/list";

const app = document.getElementById("app")!;
const nav = document.getElementById("nav")!;

let admin = false;
let authKnown = false;
let renderId = 0;

function show(content: Raw): void {
  app.innerHTML = content.value;
}

function renderNav(): void {
  const user = auth.currentUser;
  nav.innerHTML = html`
    <a href="#/">Events</a>
    ${admin ? html`<a href="#/admin">Admin</a>` : ""}
    ${user
      ? html`<span class="muted small" title="${user.email ?? ""}">${user.displayName ?? user.email ?? ""}</span> <button type="button" class="link" id="logout">Sign out</button>`
      : html`<button type="button" class="link" id="signin">Sign in</button>`}`.value;
  document.getElementById("signin")?.addEventListener("click", () => void signIn().catch((e) => console.error(e)));
  document.getElementById("logout")?.addEventListener("click", async () => {
    await logout();
    location.hash = "#/";
  });
}

/** Hash routes, so GitHub Pages never has to know about them: #/year/2026, #/event/435, #/admin/... */
/**
 * GitHub Pages lets browsers cache index.html for a while, so a tab can keep running an old build
 * long after a deploy. The current build's script is compared with the one index.html names now;
 * when they differ, the next navigation loads the new build instead.
 */
const build = document.querySelector<HTMLScriptElement>("script[type=module][src]")?.src;
let stale = false;

async function checkForUpdate(): Promise<void> {
  try {
    const page = await (await fetch(location.pathname, { cache: "no-store" })).text();
    const current = page.match(/<script[^>]+type="module"[^>]+src="([^"]+)"/)?.[1];
    stale = !!build && !!current && new URL(current, location.href).href !== build;
  } catch {
    // Offline or blocked: keep running what is loaded.
  }
}

void checkForUpdate();
setInterval(() => void checkForUpdate(), 5 * 60_000);
document.addEventListener("visibilitychange", () => document.visibilityState === "visible" && void checkForUpdate());

async function route(): Promise<void> {
  if (stale) {
    location.reload();
    return;
  }
  const mine = ++renderId;
  stopListening();
  const [path, search = ""] = location.hash.replace(/^#/, "").split("?");
  const params = new URLSearchParams(search);
  const parts = path.split("/").filter(Boolean);
  const thisYear = new Date().getFullYear();

  const set = (content: Raw, wire?: () => void) => {
    if (mine !== renderId) return;
    show(content);
    wire?.();
    window.scrollTo(0, 0);
  };

  try {
    if (parts[0] === "event" && parts[1]) {
      set(await eventPage(parts[1], params, admin), () => wireEvent(parts[1]));
    } else if (parts[0] === "admin") {
      if (!authKnown) return;
      if (!admin) {
        set(loginPage(params.has("failed")), () =>
          wireLogin((ok) => {
            // On success the auth listener re-renders; a failure may land on the same hash, so render it directly.
            if (!ok) {
              location.hash = "#/admin?failed";
              void route();
            }
          }),
        );
      } else if (parts[1] === "event" && parts[2]) {
        set(await editPage(parts[2]), () => wireEdit(parts[2], (id) => (location.hash = `#/event/${id}`)));
      } else if (parts[1] === "new") {
        set(await editPage(null), () => wireEdit(null, (id) => (location.hash = `#/event/${id}`)));
      } else {
        set(await dashboardPage(Number(params.get("year")) || thisYear));
      }
    } else {
      const year = parts[0] === "year" ? Number(parts[1]) || thisYear : thisYear;
      set(await listPage(year, readFilter(params)), () => wireList(year));
    }
  } catch (e) {
    console.error(e);
    set(html`<h1>Something went wrong</h1><p class="muted">${(e as Error).message}</p><p><a href="#/">Back to the list</a></p>`);
  }
}

onAuthStateChanged(auth, async (user: User | null) => {
  admin = user ? await isAdmin(user.uid).catch(() => false) : false;
  // A reader signed in with Google is not an admin; the admin pages ask for the admin login instead.
  if (user && !admin && location.hash.startsWith("#/admin") && !location.hash.includes("failed")) {
    location.hash = "#/admin?failed";
  }
  authKnown = true;
  renderNav();
  await route();
});

window.addEventListener("hashchange", route);

// A schedule image opens full screen on click, and closes the same way.
app.addEventListener("click", (ev) => {
  const img = ev.target as HTMLElement;
  if (img.classList.contains("zoomable")) img.classList.toggle("zoomed");
});
renderNav();
void route();
