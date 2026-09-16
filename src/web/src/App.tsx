import { FormEvent, useEffect, useState } from "react";
import { Administration } from "./Administration";
import { call, CurrentUser, message, Project } from "./api";
import { SupportWorkspace } from "./SupportWorkspace";
import { SolutionsWorkspace } from "./SolutionsWorkspace";

type View = "tickets" | "solutions" | "administration";

export function App() {
  const [user, setUser] = useState<CurrentUser | null>();
  const [projects, setProjects] = useState<Project[]>([]);
  const [view, setView] = useState<View>("tickets");
  const [error, setError] = useState("");

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
  }

  if (user === undefined) return <main className="center">Loading helpaffe…</main>;
  if (user === null) return <main className="auth">
    <form className="panel auth-panel" onSubmit={signIn}>
      <span className="wordmark">helpaffe</span>
      <p className="eyebrow">Backoffice</p>
      <h1>Sign in to support.</h1>
      <label>Email<input name="email" type="email" autoComplete="username" required /></label>
      <label>Password<input name="password" type="password" autoComplete="current-password" required /></label>
      {error && <p className="error" role="alert">{error}</p>}
      <button type="submit">Sign in</button>
    </form>
  </main>;

  return <main className="shell">
    <header className="topbar">
      <div className="brand-group">
        <span className="wordmark">helpaffe</span>
        <nav aria-label="Primary navigation">
          <button className={view === "tickets" ? "nav-link active" : "nav-link"} onClick={() => setView("tickets")}>Work queue</button>
          <button className={view === "solutions" ? "nav-link active" : "nav-link"} onClick={() => setView("solutions")}>Solutions</button>
          <button className={view === "administration" ? "nav-link active" : "nav-link"} onClick={() => setView("administration")}>{user.role === "administrator" ? "Administration" : "Agent access"}</button>
        </nav>
      </div>
      <div className="account"><span>{user.name}</span><button className="secondary compact" onClick={signOut}>Sign out</button></div>
    </header>
    {error && <p className="error global-banner" role="alert">{error}</p>}
    {view === "tickets" && <SupportWorkspace user={user} projects={projects} />}
    {view === "solutions" && <SolutionsWorkspace projects={projects} />}
    {view === "administration" && <Administration user={user} projects={projects} onProjectsChanged={refreshProjects} />}
  </main>;
}
