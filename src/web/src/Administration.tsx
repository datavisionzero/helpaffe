import { FormEvent, useEffect, useState } from "react";
import { call, CurrentUser, message, Project, Role } from "./api";

type ManagedUser = CurrentUser & { is_active: boolean; project_ids: string[] };
type AgentCredential = {
  id: string;
  user_id: string;
  user_name: string;
  name: string;
  token_prefix: string;
  all_projects: boolean;
  project_ids: string[];
  is_active: boolean;
};
type ProductKey = {
  id: string;
  project_id: string;
  project_name: string;
  name: string;
  token_prefix: string;
  is_active: boolean;
};
type CreatedCredential<T> = { credential: T; token: string };

type Props = {
  user: CurrentUser;
  projects: Project[];
  onProjectsChanged: () => Promise<void>;
};

export function Administration({ user, projects, onProjectsChanged }: Props) {
  const [users, setUsers] = useState<ManagedUser[]>([]);
  const [agents, setAgents] = useState<AgentCredential[]>([]);
  const [productKeys, setProductKeys] = useState<ProductKey[]>([]);
  const [agentAllProjects, setAgentAllProjects] = useState(true);
  const [revealedSecret, setRevealedSecret] = useState<{ label: string; token: string } | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    void refresh();
  }, [user.id, user.role]);

  async function refresh() {
    try {
      setError("");
      setAgents(await call<AgentCredential[]>("/agents"));
      if (user.role === "administrator") {
        setUsers(await call<ManagedUser[]>("/users"));
        setProductKeys(await call<ProductKey[]>("/product-keys"));
      }
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function createProject(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    try {
      await call<Project>("/projects", { method: "POST", body: JSON.stringify(Object.fromEntries(data)) });
      form.reset();
      await onProjectsChanged();
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function createUser(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    try {
      await call<ManagedUser>("/users", { method: "POST", body: JSON.stringify(Object.fromEntries(data)) });
      form.reset();
      await refresh();
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function createAgent(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    try {
      const created = await call<CreatedCredential<AgentCredential>>("/agents", {
        method: "POST",
        body: JSON.stringify({
          name: data.get("name"),
          user_id: data.get("userId") || undefined,
          all_projects: agentAllProjects,
          project_ids: agentAllProjects ? [] : data.getAll("projectId"),
        }),
      });
      setRevealedSecret({ label: `Token for ${created.credential.name}`, token: created.token });
      form.reset();
      setAgentAllProjects(true);
      await refresh();
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function revokeAgent(credential: AgentCredential) {
    try {
      await call<void>(`/agents/${credential.id}`, { method: "DELETE" });
      await refresh();
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function createProductKey(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    try {
      const projectId = String(data.get("projectId"));
      const created = await call<CreatedCredential<ProductKey>>(`/projects/${projectId}/product-keys`, {
        method: "POST",
        body: JSON.stringify({ name: data.get("name") }),
      });
      setRevealedSecret({ label: `Product key for ${created.credential.project_name}`, token: created.token });
      form.reset();
      await refresh();
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function revokeProductKey(credential: ProductKey) {
    try {
      await call<void>(`/product-keys/${credential.id}`, { method: "DELETE" });
      await refresh();
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function updateUser(id: string, update: { role?: Role; is_active?: boolean }) {
    try {
      await call<ManagedUser>(`/users/${id}`, { method: "PATCH", body: JSON.stringify(update) });
      await refresh();
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function changeAccess(managedUser: ManagedUser, projectId: string, grant: boolean) {
    try {
      await call<void>(`/users/${managedUser.id}/projects/${projectId}`, { method: grant ? "PUT" : "DELETE" });
      await refresh();
    } catch (reason) {
      setError(message(reason));
    }
  }

  return <div className="workspace admin-workspace">
    <section className="intro compact-intro">
      <p className="eyebrow">{user.role}</p>
      <h1>{user.role === "administrator" ? "Administration" : "Agent access"}</h1>
      <p>{user.role === "administrator" ? "Manage people, projects, and scoped credentials." : "Create and revoke credentials for your own support agents."}</p>
    </section>
    {error && <p className="error banner" role="alert">{error}</p>}
    {revealedSecret && <aside className="secret" role="status">
      <div><p className="eyebrow">Shown once</p><h2>{revealedSecret.label}</h2><p>Copy this credential now. Only its prefix will be stored and shown again.</p></div>
      <code>{revealedSecret.token}</code>
      <button className="secondary" onClick={() => setRevealedSecret(null)}>I have copied it</button>
    </aside>}
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
            <button className="secondary" onClick={() => void updateUser(managedUser.id, { is_active: !managedUser.is_active })}>{managedUser.is_active ? "Deactivate" : "Reactivate"}</button>
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
    <section className="section" aria-labelledby="agents-heading">
      <div className="section-heading"><div><p className="eyebrow">Delegation</p><h2 id="agents-heading">Agent credentials</h2></div><span className="count">{agents.length}</span></div>
      <p className="section-copy">Each agent gets its own revocable token and can never exceed its owner's current project access.</p>
      <div className="people-list">
        {agents.map(credential => <article className={`person ${credential.is_active ? "" : "inactive"}`} key={credential.id}>
          <div className="person-summary"><div><h3>{credential.name}</h3><p>{credential.user_name} · <code>{credential.token_prefix}…</code></p></div><span className="status">{credential.is_active ? "Active" : "Revoked"}</span></div>
          <p className="scope">{credential.all_projects ? "All projects the owner may access, including future access" : `${credential.project_ids.length} selected project${credential.project_ids.length === 1 ? "" : "s"}`}</p>
          {credential.is_active && <button className="secondary" onClick={() => void revokeAgent(credential)}>Revoke agent</button>}
        </article>)}
      </div>
      <form className="create-user" onSubmit={createAgent}>
        <h3>Create an agent credential</h3>
        <div className="form-grid">
          <label>Agent name<input name="name" required maxLength={200} placeholder="Triage agent" /></label>
          {user.role === "administrator" && <label>Owner<select name="userId" defaultValue={user.id}>{users.filter(item => item.is_active).map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>}
        </div>
        <label className="check-row"><input type="checkbox" checked={agentAllProjects} onChange={event => setAgentAllProjects(event.target.checked)} />All projects the owner may access</label>
        {!agentAllProjects && <fieldset><legend>Selected projects</legend><div className="access-list">
          {projects.map(project => <label key={project.id}><input type="checkbox" name="projectId" value={project.id} />{project.name}</label>)}
        </div></fieldset>}
        <button type="submit">Create agent token</button>
      </form>
    </section>
    {user.role === "administrator" && <section className="section" aria-labelledby="product-keys-heading">
      <div className="section-heading"><div><p className="eyebrow">Product integration</p><h2 id="product-keys-heading">Product API keys</h2></div><span className="count">{productKeys.length}</span></div>
      <p className="section-copy">Product keys are project-bound and never authorize backoffice or agent operations.</p>
      <div className="people-list">
        {productKeys.map(credential => <article className={`person ${credential.is_active ? "" : "inactive"}`} key={credential.id}>
          <div className="person-summary"><div><h3>{credential.name}</h3><p>{credential.project_name} · <code>{credential.token_prefix}…</code></p></div><span className="status">{credential.is_active ? "Active" : "Revoked"}</span></div>
          {credential.is_active && <button className="secondary credential-action" onClick={() => void revokeProductKey(credential)}>Revoke key</button>}
        </article>)}
      </div>
      <form className="inline-form product-key-form" onSubmit={createProductKey}>
        <label>Key name<input name="name" required maxLength={200} placeholder="Production" /></label>
        <label>Project<select name="projectId" required>{projects.map(project => <option key={project.id} value={project.id}>{project.name}</option>)}</select></label>
        <button type="submit" disabled={projects.length === 0}>Create product key</button>
      </form>
    </section>}
  </div>;
}
