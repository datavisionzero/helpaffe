import { FormEvent, useEffect, useState } from "react";

type Role = "administrator" | "support";
type CurrentUser = { id: string; email: string; name: string; role: Role };
type Project = { id: string; key: string; name: string };
type ManagedUser = CurrentUser & { is_active: boolean; project_ids: string[] };

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api/backoffice${path}`, {
    credentials: "same-origin",
    ...init,
    headers: {
      ...(init?.method && init.method !== "GET"
        ? { "X-Helpaffe-CSRF": "1", "Content-Type": "application/json" }
        : {}),
      ...init?.headers,
    },
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { detail?: string } | null;
    throw new Error(problem?.detail ?? (response.status === 401
      ? "The email address or password is incorrect."
      : "The request failed."));
  }
  return (response.status === 204 ? undefined : await response.json()) as T;
}

export function App() {
  const [user, setUser] = useState<CurrentUser | null>();
  const [projects, setProjects] = useState<Project[]>([]);
  const [users, setUsers] = useState<ManagedUser[]>([]);
  const [error, setError] = useState("");

  useEffect(() => {
    call<CurrentUser>("/me").then(setUser).catch(() => setUser(null));
  }, []);

  useEffect(() => {
    if (!user) return;
    void refresh(user);
  }, [user]);

  async function refresh(current = user) {
    if (!current) return;
    try {
      setError("");
      const availableProjects = await call<Project[]>("/projects");
      setProjects(availableProjects);
      setUsers(current.role === "administrator" ? await call<ManagedUser[]>("/users") : []);
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
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function signOut() {
    await call<void>("/session", { method: "DELETE" });
    setUser(null);
    setProjects([]);
    setUsers([]);
  }

  async function createUser(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    try {
      await call<ManagedUser>("/users", {
        method: "POST",
        body: JSON.stringify(Object.fromEntries(data)),
      });
      form.reset();
      await refresh();
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function createProject(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    try {
      await call<Project>("/projects", {
        method: "POST",
        body: JSON.stringify(Object.fromEntries(data)),
      });
      form.reset();
      await refresh();
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function updateUser(id: string, update: { role?: Role; isActive?: boolean }) {
    try {
      await call<ManagedUser>(`/users/${id}`, { method: "PATCH", body: JSON.stringify(update) });
      await refresh();
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function changeAccess(managedUser: ManagedUser, projectId: string, grant: boolean) {
    try {
      await call<void>(`/users/${managedUser.id}/projects/${projectId}`, {
        method: grant ? "PUT" : "DELETE",
      });
      await refresh();
    } catch (reason) {
      setError(message(reason));
    }
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
      <span className="wordmark">helpaffe</span>
      <div className="account"><span>{user.name}</span><button className="secondary" onClick={signOut}>Sign out</button></div>
    </header>
    <div className="workspace">
      <section className="intro">
        <p className="eyebrow">{user.role}</p>
        <h1>Support workspace</h1>
        <p>{user.role === "administrator" ? "Manage the people and projects that can use helpaffe." : "Projects assigned to you appear below."}</p>
      </section>
      {error && <p className="error banner" role="alert">{error}</p>}
      <section className="section" aria-labelledby="projects-heading">
        <div className="section-heading"><div><p className="eyebrow">Access</p><h2 id="projects-heading">Projects</h2></div><span className="count">{projects.length}</span></div>
        {projects.length === 0 ? <p className="empty">No projects are available.</p> : <div className="project-grid">
          {projects.map(project => <article className="project-card" key={project.id}><span>{project.key}</span><h3>{project.name}</h3></article>)}
        </div>}
        {user.role === "administrator" && <form className="inline-form" onSubmit={createProject}>
          <label>Project key<input name="key" required maxLength={32} placeholder="DOCS" /></label>
          <label>Project name<input name="name" required maxLength={200} placeholder="Documentation" /></label>
          <button type="submit">Add project</button>
        </form>}
      </section>
      {user.role === "administrator" && <section className="section" aria-labelledby="users-heading">
        <div className="section-heading"><div><p className="eyebrow">Administration</p><h2 id="users-heading">People</h2></div><span className="count">{users.length}</span></div>
        <div className="people-list">
          {users.map(managedUser => <article className={`person ${managedUser.is_active ? "" : "inactive"}`} key={managedUser.id}>
            <div className="person-summary"><div><h3>{managedUser.name}</h3><p>{managedUser.email}</p></div><span className="status">{managedUser.is_active ? "Active" : "Inactive"}</span></div>
            <div className="person-controls">
              <label>Role<select aria-label={`Role for ${managedUser.name}`} value={managedUser.role} onChange={event => void updateUser(managedUser.id, { role: event.target.value as Role })}>
                <option value="administrator">Administrator</option><option value="support">Support</option>
              </select></label>
              <button className="secondary" onClick={() => void updateUser(managedUser.id, { isActive: !managedUser.is_active })}>{managedUser.is_active ? "Deactivate" : "Reactivate"}</button>
            </div>
            {managedUser.role === "support" && <fieldset><legend>Project access</legend><div className="access-list">
              {projects.length === 0 ? <span className="muted">Create a project before assigning access.</span> : projects.map(project => <label key={project.id}>
                <input type="checkbox" checked={managedUser.project_ids.includes(project.id)} onChange={event => void changeAccess(managedUser, project.id, event.target.checked)} />{project.name}
              </label>)}
            </div></fieldset>}
          </article>)}
        </div>
        <form className="create-user" onSubmit={createUser}>
          <h3>Add a person</h3>
          <div className="form-grid">
            <label>Name<input name="name" required maxLength={200} /></label>
            <label>Email<input name="email" type="email" required maxLength={320} /></label>
            <label>Temporary password<input name="password" type="password" required minLength={12} autoComplete="new-password" /></label>
            <label>Role<select name="role" defaultValue="support"><option value="support">Support</option><option value="administrator">Administrator</option></select></label>
          </div>
          <button type="submit">Add person</button>
        </form>
      </section>}
    </div>
  </main>;
}

function message(reason: unknown) {
  return reason instanceof Error ? reason.message : "The request failed.";
}
