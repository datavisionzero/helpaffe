import { FormEvent, useEffect, useRef, useState } from "react";
import { call, message, Project, requestKey, RequestError } from "./api";

type SolutionSummary = {
  id: string;
  project_id: string;
  key: string;
  title: string;
  version: number;
  created_at: string;
  updated_at: string;
};

type Solution = SolutionSummary & { markdown: string };
type SolutionPage = { items: SolutionSummary[]; next_cursor: string | null };
type Draft = { key: string; title: string; markdown: string };

const emptyDraft: Draft = { key: "", title: "", markdown: "" };

export function SolutionsWorkspace({ projects }: { projects: Project[] }) {
  const pane = useRef<HTMLElement>(null);
  const [projectId, setProjectId] = useState(projects[0]?.id ?? "");
  const [searchInput, setSearchInput] = useState("");
  const [search, setSearch] = useState("");
  const [solutions, setSolutions] = useState<SolutionSummary[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [detail, setDetail] = useState<Solution | null>(null);
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [editing, setEditing] = useState<"create" | "update" | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [conflict, setConflict] = useState<{ detail: string; currentVersion?: number } | null>(null);
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (!projects.some(project => project.id === projectId)) {
      setProjectId(projects[0]?.id ?? "");
    }
  }, [projects, projectId]);

  useEffect(() => {
    setDetail(null);
    setEditing(null);
    setConflict(null);
    setConfirmDelete(false);
    if (projectId) void loadSolutions();
    else {
      setSolutions([]);
      setNextCursor(null);
    }
  }, [projectId, search]);

  useEffect(() => {
    if (pane.current) pane.current.scrollTop = 0;
  }, [editing, detail?.key, detail?.version]);

  async function loadSolutions(cursor?: string) {
    if (!projectId) return;
    try {
      setLoading(true);
      setError("");
      const parameters = new URLSearchParams();
      if (search) parameters.set("search", search);
      if (cursor) parameters.set("cursor", cursor);
      const suffix = parameters.size ? `?${parameters.toString()}` : "";
      const page = await call<SolutionPage>(`/projects/${encodeURIComponent(projectId)}/solutions${suffix}`);
      setSolutions(current => cursor ? [...current, ...page.items] : page.items);
      setNextCursor(page.next_cursor);
    } catch (reason) {
      setError(message(reason));
    } finally {
      setLoading(false);
    }
  }

  async function openSolution(key: string, preserveDraft = false) {
    if (!projectId) return;
    try {
      setError("");
      const selected = await call<Solution>(solutionPath(projectId, key));
      setDetail(selected);
      setEditing(preserveDraft ? "update" : null);
      setConfirmDelete(false);
      setConflict(null);
      if (!preserveDraft) setDraft(toDraft(selected));
    } catch (reason) {
      setError(message(reason));
    }
  }

  function beginCreate() {
    setDetail(null);
    setDraft(emptyDraft);
    setEditing("create");
    setConflict(null);
    setConfirmDelete(false);
    setError("");
    setNotice("");
  }

  function beginUpdate() {
    if (!detail) return;
    setDraft(toDraft(detail));
    setEditing("update");
    setConflict(null);
    setConfirmDelete(false);
    setError("");
    setNotice("");
  }

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!projectId || !editing) return;
    if (!draft.title.trim() || !draft.markdown.trim() || editing === "create" && !draft.key.trim()) {
      setError("Key, title, and Markdown are required.");
      return;
    }

    try {
      setError("");
      setNotice("");
      const path = editing === "create"
        ? `/projects/${encodeURIComponent(projectId)}/solutions`
        : solutionPath(projectId, detail!.key);
      const saved = await call<Solution>(path, {
        method: editing === "create" ? "POST" : "PUT",
        headers: {
          "Idempotency-Key": requestKey(),
          ...(editing === "update" ? { "If-Match": `"${detail!.version}"` } : {}),
        },
        body: JSON.stringify(editing === "create" ? draft : { title: draft.title, markdown: draft.markdown }),
      });
      setDetail(saved);
      setDraft(toDraft(saved));
      setEditing(null);
      setConflict(null);
      setNotice(editing === "create" ? "Solution article created." : "Solution article updated.");
      await loadSolutions();
    } catch (reason) {
      if (reason instanceof RequestError && reason.status === 412) {
        setConflict({ detail: reason.message, currentVersion: reason.currentVersion });
      } else {
        setError(message(reason));
      }
    }
  }

  async function deleteSolution() {
    if (!projectId || !detail) return;
    try {
      setError("");
      await call<void>(solutionPath(projectId, detail.key), {
        method: "DELETE",
        headers: { "If-Match": `"${detail.version}"`, "Idempotency-Key": requestKey() },
      });
      setDetail(null);
      setEditing(null);
      setConfirmDelete(false);
      setConflict(null);
      setNotice("Solution article deleted.");
      await loadSolutions();
    } catch (reason) {
      if (reason instanceof RequestError && reason.status === 412) {
        setConflict({ detail: reason.message, currentVersion: reason.currentVersion });
        setConfirmDelete(false);
      } else {
        setError(message(reason));
      }
    }
  }

  const selectedProject = projects.find(project => project.id === projectId);

  return <div className="solutions-workspace">
    <aside className="solutions-sidebar">
      <div className="solutions-heading">
        <div><p className="eyebrow">Shared knowledge</p><h1>Solutions</h1></div>
        <button onClick={beginCreate} disabled={!projectId}>New article</button>
      </div>
      <form className="solutions-filters" onSubmit={event => { event.preventDefault(); setSearch(searchInput.trim()); }}>
        <label>Project<select value={projectId} onChange={event => setProjectId(event.target.value)}>
          {projects.length === 0 && <option value="">No projects available</option>}
          {projects.map(project => <option key={project.id} value={project.id}>{project.key} · {project.name}</option>)}
        </select></label>
        <label>Search<div className="search-control"><input value={searchInput} onChange={event => setSearchInput(event.target.value)} placeholder="Title or Markdown…" /><button className="secondary" type="submit">Search</button></div></label>
      </form>
      {notice && <p className="notice" role="status">{notice}</p>}
      {error && !editing && <p className="error banner" role="alert">{error}</p>}
      <div className="solution-list" aria-busy={loading}>
        {loading && solutions.length === 0 && <p className="muted">Loading solutions…</p>}
        {!loading && projectId && solutions.length === 0 && <div className="queue-empty"><strong>No solutions yet.</strong><span>Create the first reusable answer or try another search.</span></div>}
        {!projectId && <div className="queue-empty"><strong>No project scope.</strong><span>You need access to a project before storing solutions.</span></div>}
        {solutions.map(solution => <button className={`solution-row ${detail?.key === solution.key ? "selected" : ""}`} key={solution.id} onClick={() => void openSolution(solution.key)}>
          <span className="solution-key">{solution.key}</span>
          <strong>{solution.title}</strong>
          <span>Updated {formatDate(solution.updated_at)} · v{solution.version}</span>
        </button>)}
      </div>
      {nextCursor && <button className="secondary load-more" disabled={loading} onClick={() => void loadSolutions(nextCursor)}>Load more</button>}
    </aside>
    <section className="solution-pane" ref={pane}>
      {editing ? <form className="solution-editor" onSubmit={save}>
        <div className="solution-pane-heading">
          <div><p className="eyebrow">{selectedProject?.key ?? "Project"}</p><h2>{editing === "create" ? "New solution" : "Edit solution"}</h2></div>
          <button className="secondary compact" type="button" onClick={() => { setEditing(null); setConflict(null); if (!detail) setDraft(emptyDraft); }}>Cancel</button>
        </div>
        {conflict && <div className="conflict" role="alert"><div><strong>This article changed while you were editing.</strong><span>{conflict.detail}{conflict.currentVersion ? ` Current version: ${conflict.currentVersion}.` : ""}</span></div><button className="secondary compact" type="button" onClick={() => detail && void openSolution(detail.key, true)}>Reload article</button></div>}
        {error && <p className="error banner" role="alert">{error}</p>}
        <label>Key<input value={draft.key} onChange={event => setDraft(current => ({ ...current, key: event.target.value }))} disabled={editing === "update"} required maxLength={80} pattern="[A-Za-z0-9](?:[A-Za-z0-9-]{0,78}[A-Za-z0-9])?" /></label>
        <label>Title<input value={draft.title} onChange={event => setDraft(current => ({ ...current, title: event.target.value }))} required maxLength={200} /></label>
        <label>Markdown<textarea aria-label="Markdown" value={draft.markdown} onChange={event => setDraft(current => ({ ...current, markdown: event.target.value }))} required maxLength={65536} rows={18} /></label>
        <div className="solution-actions"><button type="submit">{editing === "create" ? "Create article" : "Save changes"}</button></div>
      </form> : detail ? <article className="solution-detail">
        <header className="solution-pane-heading">
          <div><p className="solution-key">{selectedProject?.key} / {detail.key} · v{detail.version}</p><h2>{detail.title}</h2><p>Updated {formatDate(detail.updated_at)}</p></div>
          <div className="solution-actions"><button onClick={beginUpdate}>Edit article</button><button className="secondary" onClick={() => setConfirmDelete(true)}>Delete article</button></div>
        </header>
        {conflict && <div className="conflict" role="alert"><div><strong>This article changed.</strong><span>{conflict.detail}</span></div><button className="secondary compact" onClick={() => void openSolution(detail.key)}>Reload article</button></div>}
        {confirmDelete && <div className="delete-confirm" role="alert"><div><strong>Delete this solution article?</strong><span>This removes it from the project immediately.</span></div><div><button className="secondary compact" onClick={() => setConfirmDelete(false)}>Cancel</button><button className="danger compact" onClick={() => void deleteSolution()}>Delete permanently</button></div></div>}
        <pre className="solution-markdown">{detail.markdown}</pre>
      </article> : <div className="ticket-placeholder"><p className="eyebrow">{selectedProject?.key ?? "Shared knowledge"}</p><h2>Select a solution.</h2><p>Search reusable internal answers or create a new Markdown article for this project.</p></div>}
    </section>
  </div>;
}

function solutionPath(projectId: string, key: string) {
  return `/projects/${encodeURIComponent(projectId)}/solutions/${encodeURIComponent(key)}`;
}

function toDraft(solution: Solution): Draft {
  return { key: solution.key, title: solution.title, markdown: solution.markdown };
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat("en", { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}
