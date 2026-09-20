import { FormEvent, useEffect, useLayoutEffect, useRef, useState } from "react";
import { Administration } from "./Administration";
import { call, CurrentUser, message, Project } from "./api";
import { SupportWorkspace } from "./SupportWorkspace";
import { SolutionsWorkspace } from "./SolutionsWorkspace";

type View = "tickets" | "solutions" | "administration";
type Theme = "system" | "light" | "dark";

function savedTheme(): Theme {
  try {
    const value = window.localStorage.getItem("helpaffe-theme");
    return value === "light" || value === "dark" ? value : "system";
  } catch {
    return "system";
  }
}

export function App() {
  const [user, setUser] = useState<CurrentUser | null>();
  const [projects, setProjects] = useState<Project[]>([]);
  const [view, setView] = useState<View>("tickets");
  const [error, setError] = useState("");
  const [theme, setTheme] = useState<Theme>(savedTheme);
  const [menuOpen, setMenuOpen] = useState(false);
  const menuButton = useRef<HTMLButtonElement>(null);
  const firstNavigationLink = useRef<HTMLButtonElement>(null);
  const menuWasOpen = useRef(false);

  useLayoutEffect(() => {
    const system = window.matchMedia?.("(prefers-color-scheme: dark)");
    const apply = () => document.documentElement.classList.toggle("dark", theme === "dark" || (theme === "system" && Boolean(system?.matches)));
    apply();
    system?.addEventListener("change", apply);
    try { window.localStorage.setItem("helpaffe-theme", theme); } catch { /* Storage may be disabled. */ }
    return () => system?.removeEventListener("change", apply);
  }, [theme]);

  useEffect(() => {
    if (!menuOpen) return;
    const close = (event: KeyboardEvent) => { if (event.key === "Escape") setMenuOpen(false); };
    window.addEventListener("keydown", close);
    return () => window.removeEventListener("keydown", close);
  }, [menuOpen]);

  useEffect(() => {
    if (menuOpen) firstNavigationLink.current?.focus();
    else if (menuWasOpen.current) menuButton.current?.focus();
    menuWasOpen.current = menuOpen;
  }, [menuOpen]);

  useEffect(() => {
    const desktop = window.matchMedia?.("(min-width: 48.01rem)");
    const closeOnDesktop = () => { if (desktop?.matches) setMenuOpen(false); };
    desktop?.addEventListener("change", closeOnDesktop);
    return () => desktop?.removeEventListener("change", closeOnDesktop);
  }, []);

  useEffect(() => {
    call<CurrentUser>("/me").then(setUser).catch(() => setUser(null));
  }, []);

  useEffect(() => {
    if (!user) return;
    void refreshProjects();
  }, [user]);

  async function refreshProjects() {
    try {
      setError("");
      setProjects(await call<Project[]>("/projects"));
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function signIn(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    try {
      setError("");
      setUser(await call<CurrentUser>("/session", {
        method: "POST",
        body: JSON.stringify({ email: data.get("email"), password: data.get("password") }),
      }));
      setView("tickets");
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function signOut() {
    await call<void>("/session", { method: "DELETE" });
    setUser(null);
    setProjects([]);
    setView("tickets");
    setMenuOpen(false);
    scrollMobileTop();
  }

  function navigate(next: View) {
    setView(next);
    setMenuOpen(false);
    scrollMobileTop();
  }

  function scrollMobileTop() {
    if (window.matchMedia?.("(max-width: 40rem)").matches) {
      requestAnimationFrame(() => window.scrollTo(0, 0));
    }
  }

  if (user === undefined) return <main className="center">Loading helpaffe…</main>;
  if (user === null) return <main className="auth">
    <form className="panel auth-panel" onSubmit={signIn}>
      <span className="wordmark"><span className="brand-mark" aria-hidden="true" />helpaffe</span>
      <p className="eyebrow">Backoffice</p>
      <h1>Sign in to support.</h1>
      <label>Email<input name="email" type="email" autoComplete="username" required /></label>
      <label>Password<input name="password" type="password" autoComplete="current-password" required /></label>
      {error && <p className="error" role="alert">{error}</p>}
      <button type="submit">Sign in</button>
    </form>
  </main>;

  return <main className="shell">
    {menuOpen && <button type="button" className="mobile-backdrop" aria-label="Close navigation" onClick={() => setMenuOpen(false)} />}
    <aside id="app-navigation" className={`app-sidebar${menuOpen ? " open" : ""}`}>
      <div className="sidebar-brand"><span className="wordmark"><span className="brand-mark" aria-hidden="true" />helpaffe</span></div>
      <nav className="shell-nav" aria-label="Primary navigation">
        <span className="sidebar-label">Workspace</span>
        <button ref={firstNavigationLink} type="button" className={view === "tickets" ? "nav-link active" : "nav-link"} aria-current={view === "tickets" ? "page" : undefined} onClick={() => navigate("tickets")}>Work queue</button>
        <button type="button" className={view === "solutions" ? "nav-link active" : "nav-link"} aria-current={view === "solutions" ? "page" : undefined} onClick={() => navigate("solutions")}>Solutions</button>
        <button type="button" className={view === "administration" ? "nav-link active" : "nav-link"} aria-current={view === "administration" ? "page" : undefined} onClick={() => navigate("administration")}>{user.role === "administrator" ? "Administration" : "Agent access"}</button>
      </nav>
      <div className="sidebar-footer">
        <label className="theme-control">Appearance
          <select value={theme} onChange={event => setTheme(event.target.value as Theme)}>
            <option value="system">System</option>
            <option value="light">Light</option>
            <option value="dark">Dark</option>
          </select>
        </label>
        <div className="account"><span>{user.name}</span><button className="secondary compact" onClick={signOut}>Sign out</button></div>
      </div>
    </aside>
    <div className="shell-main" inert={menuOpen}>
      <header className="topbar">
        <button ref={menuButton} type="button" className="secondary menu-toggle" aria-label="Open navigation" aria-controls="app-navigation" aria-expanded={menuOpen} onClick={() => setMenuOpen(open => !open)}>☰</button>
        <div className="topbar-title">
          <span className="topbar-brand">helpaffe</span>
          <span aria-hidden="true" className="topbar-divider">/</span>
          <span>{view === "tickets" ? "Work queue" : view === "solutions" ? "Solutions" : user.role === "administrator" ? "Administration" : "Agent access"}</span>
        </div>
        <span className="topbar-user">{user.name}</span>
      </header>
      {error && <p className="error global-banner" role="alert">{error}</p>}
      {view === "tickets" && <SupportWorkspace user={user} projects={projects} />}
      {view === "solutions" && <SolutionsWorkspace projects={projects} />}
      {view === "administration" && <Administration user={user} projects={projects} onProjectsChanged={refreshProjects} />}
    </div>
  </main>;
}
