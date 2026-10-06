import "./style.css";
import { onAuthStateChanged, type User } from "firebase/auth";
import { auth, isAdmin } from "./firebase";
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
  nav.innerHTML = html`
    <a href="#/">Events</a>
    ${admin ? html`<a href="#/admin">Admin</a> <button type="button" class="link" id="logout">Sign out</button>` : ""}`.value;
  document.getElementById("logout")?.addEventListener("click", async () => {
    await logout();
    location.hash = "#/";
  });
}

/** Hash routes, so GitHub Pages never has to know about them: #/year/2026, #/event/435, #/admin/... */
async function route(): Promise<void> {
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
  if (user && !admin && location.hash.startsWith("#/admin")) {
    await logout();
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
