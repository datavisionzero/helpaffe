import { FormEvent, useEffect, useState } from "react";
import { call, CurrentUser, message, Project, requestKey, RequestError } from "./api";

type TicketStatus = "open" | "in_progress" | "waiting_for_customer" | "resolved";
type TicketPriority = "normal" | "urgent";
type Assignee = { id: string; name: string };
type TicketSummary = {
  id: string;
  number: string;
  project: Project;
  subject: string;
  status: TicketStatus;
  priority: TicketPriority;
  version: number;
  assignee: Assignee | null;
  created_at: string;
  updated_at: string;
  last_customer_reply_at: string;
  waiting_since: string;
};
type ConversationEntry = {
  id: string;
  sequence: number;
  kind: "customer_message" | "public_reply" | "internal_note" | "system_event";
  body: string;
  is_public: boolean;
  created_at: string;
  actor: { user_name: string | null; agent_name: string | null } | null;
};
type TicketDetail = {
  summary: TicketSummary;
  requester: { external_user_id: string; name: string; email: string };
  support_instructions: string;
  conversation: ConversationEntry[];
};
type TicketPage = { items: TicketSummary[]; next_cursor: string | null };
type Queue = TicketStatus | "mine";
type FieldDraft = { status: TicketStatus; priority: TicketPriority; assigneeId: string };

const queues: { value: Queue; label: string }[] = [
  { value: "open", label: "Open" },
  { value: "in_progress", label: "In progress" },
  { value: "waiting_for_customer", label: "Waiting for customer" },
  { value: "resolved", label: "Resolved" },
  { value: "mine", label: "My tickets" },
];

export function SupportWorkspace({ user, projects }: { user: CurrentUser; projects: Project[] }) {
  const [queue, setQueue] = useState<Queue>("open");
  const [projectId, setProjectId] = useState("");
  const [priority, setPriority] = useState("");
  const [assigneeId, setAssigneeId] = useState("");
  const [searchInput, setSearchInput] = useState("");
  const [search, setSearch] = useState("");
  const [tickets, setTickets] = useState<TicketSummary[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [assignees, setAssignees] = useState<Assignee[]>([]);
  const [ticketAssignees, setTicketAssignees] = useState<Assignee[]>([]);
  const [detail, setDetail] = useState<TicketDetail | null>(null);
  const [fieldDraft, setFieldDraft] = useState<FieldDraft | null>(null);
  const [reply, setReply] = useState("");
  const [replyStatus, setReplyStatus] = useState<TicketStatus>("waiting_for_customer");
  const [note, setNote] = useState("");
  const [composer, setComposer] = useState<"reply" | "note">("reply");
  const [conflict, setConflict] = useState<{ detail: string; currentVersion?: number } | null>(null);
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    void loadAssignees(projectId, setAssignees);
  }, [projectId]);

  useEffect(() => {
    void loadTickets();
  }, [queue, projectId, priority, assigneeId, search]);

  async function loadAssignees(selectedProject: string, apply: (values: Assignee[]) => void) {
    try {
      const query = selectedProject ? `?project_id=${encodeURIComponent(selectedProject)}` : "";
      apply(await call<Assignee[]>(`/assignees${query}`));
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function loadTickets(cursor?: string) {
    try {
      setLoading(true);
      setError("");
      const parameters = new URLSearchParams();
      if (queue === "mine") parameters.set("mine", "true");
      else parameters.set("status", queue);
      if (projectId) parameters.set("project_id", projectId);
      if (priority) parameters.set("priority", priority);
      if (assigneeId && queue !== "mine") parameters.set("assignee_id", assigneeId);
      if (search) parameters.set("search", search);
      if (cursor) parameters.set("cursor", cursor);
      const page = await call<TicketPage>(`/tickets?${parameters.toString()}`);
      setTickets(current => cursor ? [...current, ...page.items] : page.items);
      setNextCursor(page.next_cursor);
    } catch (reason) {
      setError(message(reason));
    } finally {
      setLoading(false);
    }
  }

  async function openTicket(number: string, preserveDrafts = false) {
    try {
      setError("");
      const selected = await call<TicketDetail>(`/tickets/${encodeURIComponent(number)}`);
      setDetail(selected);
      if (!preserveDrafts) {
        setFieldDraft(fieldsFrom(selected.summary));
        setReply("");
        setNote("");
      }
      setConflict(null);
      void loadAssignees(selected.summary.project.id, setTicketAssignees);
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function acquireNext() {
    try {
      setError("");
      setNotice("");
      const acquired = await call<TicketDetail>("/tickets/next", {
        method: "POST",
        headers: { "Idempotency-Key": requestKey() },
        body: JSON.stringify({ project_id: projectId || null }),
      });
      setDetail(acquired);
      setFieldDraft(fieldsFrom(acquired.summary));
      setConflict(null);
      setNotice(`${acquired.summary.number} is now assigned to you.`);
      void loadAssignees(acquired.summary.project.id, setTicketAssignees);
      await loadTickets();
    } catch (reason) {
      if (reason instanceof RequestError && reason.type.endsWith("/no-ticket")) {
        setNotice("There is no eligible open ticket in this project scope.");
      } else {
        setError(message(reason));
      }
    }
  }

  function handleWriteError(reason: unknown) {
    if (reason instanceof RequestError && reason.status === 412) {
      setConflict({ detail: reason.message, currentVersion: reason.currentVersion });
      return;
    }
    setError(message(reason));
  }

  async function applyMutation(action: () => Promise<TicketDetail>, clear?: () => void) {
    try {
      setError("");
      const updated = await action();
      setDetail(updated);
      setFieldDraft(fieldsFrom(updated.summary));
      setConflict(null);
      clear?.();
      await loadTickets();
    } catch (reason) {
      handleWriteError(reason);
    }
  }

  async function saveFields(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!detail || !fieldDraft) return;
    const assignmentChanged = fieldDraft.assigneeId !== (detail.summary.assignee?.id ?? "");
    await applyMutation(() => call<TicketDetail>(`/tickets/${encodeURIComponent(detail.summary.number)}`, {
      method: "PATCH",
      headers: { "If-Match": `"${detail.summary.version}"` },
      body: JSON.stringify({
        status: fieldDraft.status,
        priority: fieldDraft.priority,
        assignee_id: fieldDraft.assigneeId || undefined,
        clear_assignee: assignmentChanged && !fieldDraft.assigneeId,
      }),
    }));
  }

  async function sendReply(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!detail || !reply.trim()) return;
    await applyMutation(() => call<TicketDetail>(`/tickets/${encodeURIComponent(detail.summary.number)}/replies`, {
      method: "POST",
      headers: { "If-Match": `"${detail.summary.version}"`, "Idempotency-Key": requestKey() },
      body: JSON.stringify({ message: reply, status: replyStatus }),
    }), () => setReply(""));
  }

  async function addNote(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!detail || !note.trim()) return;
    await applyMutation(() => call<TicketDetail>(`/tickets/${encodeURIComponent(detail.summary.number)}/notes`, {
      method: "POST",
      headers: { "If-Match": `"${detail.summary.version}"`, "Idempotency-Key": requestKey() },
      body: JSON.stringify({ message: note }),
    }), () => setNote(""));
  }

  function selectQueue(value: Queue) {
    setQueue(value);
    setDetail(null);
    setConflict(null);
    setNotice("");
  }

  const hasNewCustomerActivity = detail ? customerActivity(detail.summary) : false;
  const fieldsChanged = detail && fieldDraft && (
    fieldDraft.status !== detail.summary.status ||
    fieldDraft.priority !== detail.summary.priority ||
    fieldDraft.assigneeId !== (detail.summary.assignee?.id ?? "")
  );

  return <div className="support-workspace">
    <aside className="queue-sidebar">
      <div className="queue-heading">
        <div><p className="eyebrow">Shared support</p><h1>Work queue</h1></div>
        <button onClick={() => void acquireNext()}>Next ticket</button>
      </div>
      <div className="queue-tabs" role="tablist" aria-label="Ticket status">
        {queues.map(item => <button key={item.value} role="tab" aria-selected={queue === item.value} className={queue === item.value ? "active" : ""} onClick={() => selectQueue(item.value)}>{item.label}</button>)}
      </div>
      <form className="queue-filters" onSubmit={event => { event.preventDefault(); setSearch(searchInput.trim()); }}>
        <label>Project<select value={projectId} onChange={event => { setProjectId(event.target.value); setAssigneeId(""); }}>
          <option value="">All projects</option>{projects.map(project => <option key={project.id} value={project.id}>{project.key} · {project.name}</option>)}
        </select></label>
        <div className="filter-row">
          <label>Priority<select value={priority} onChange={event => setPriority(event.target.value)}><option value="">Any priority</option><option value="urgent">Urgent</option><option value="normal">Normal</option></select></label>
          <label>Assignee<select value={assigneeId} disabled={queue === "mine"} onChange={event => setAssigneeId(event.target.value)}><option value="">Anyone</option>{assignees.map(person => <option key={person.id} value={person.id}>{person.name}</option>)}</select></label>
        </div>
        <label>Search<div className="search-control"><input value={searchInput} onChange={event => setSearchInput(event.target.value)} placeholder="Number, subject, requester…" /><button className="secondary" type="submit">Search</button></div></label>
      </form>
      {notice && <p className="notice" role="status">{notice}</p>}
      {error && <p className="error banner" role="alert">{error}</p>}
      <div className="ticket-list" aria-busy={loading}>
        {!loading && tickets.length === 0 && <div className="queue-empty"><strong>No tickets here.</strong><span>Try another status or project.</span></div>}
        {tickets.map(ticket => <button className={`ticket-row ${detail?.summary.number === ticket.number ? "selected" : ""}`} key={ticket.id} onClick={() => void openTicket(ticket.number)}>
          <span className="ticket-row-top"><span className="ticket-number">{ticket.number}</span><span className={`priority ${ticket.priority}`}>{ticket.priority}</span></span>
          <strong>{ticket.subject}</strong>
          <span className="ticket-meta"><span>{ticket.project.key}</span><span>{ticket.assignee?.name ?? "Unassigned"}</span><time>{relativeTime(ticket.updated_at)}</time></span>
          {customerActivity(ticket) && <span className="activity-dot">New customer activity</span>}
        </button>)}
      </div>
      {nextCursor && <button className="secondary load-more" onClick={() => void loadTickets(nextCursor)}>Load more</button>}
    </aside>
    <section className="ticket-pane" aria-label="Ticket detail">
      {!detail ? <div className="ticket-placeholder"><p className="eyebrow">Ticket context</p><h2>Select a ticket</h2><p>Open a ticket from the shared queue, or take the next eligible request.</p></div> : <>
        <header className="ticket-header">
          <div>
            <div className="ticket-kicker"><span>{detail.summary.project.key}</span><span>{detail.summary.number}</span><span className={`priority ${detail.summary.priority}`}>{detail.summary.priority}</span></div>
            <h2>{detail.summary.subject}</h2>
            <p>{detail.requester.name} · <a href={`mailto:${detail.requester.email}`}>{detail.requester.email}</a></p>
          </div>
          <button className="secondary compact" onClick={() => void openTicket(detail.summary.number, true)}>Refresh</button>
        </header>
        {hasNewCustomerActivity && <div className="customer-activity" role="status"><strong>New customer activity</strong><span>The latest message came from the requester. Review it before replying.</span></div>}
        {conflict && <div className="conflict" role="alert">
          <div><strong>This ticket changed while you were working.</strong><span>{conflict.detail} Your draft has been kept{conflict.currentVersion ? `; the current version is ${conflict.currentVersion}` : ""}.</span></div>
          <button onClick={() => void openTicket(detail.summary.number, true)}>Reload ticket</button>
        </div>}
        <div className="ticket-layout">
          <div className="conversation-column">
            <ol className="conversation" aria-label="Ticket conversation">
              {detail.conversation.map(entry => <li className={`conversation-entry ${entry.kind}`} key={entry.id}>
                <div className="entry-heading"><span className="entry-kind">{kindLabel(entry.kind)}</span><time>{formatDate(entry.created_at)}</time></div>
                <p>{entry.body}</p>
                {entry.actor && <span className="entry-actor">{entry.actor.user_name}{entry.actor.agent_name ? ` via ${entry.actor.agent_name}` : ""}</span>}
              </li>)}
            </ol>
            <div className="composer">
              <div className="composer-tabs" role="tablist" aria-label="Compose message">
                <button type="button" role="tab" aria-selected={composer === "reply"} className={composer === "reply" ? "active" : ""} onClick={() => setComposer("reply")}>Public reply</button>
                <button type="button" role="tab" aria-selected={composer === "note"} className={composer === "note" ? "active" : ""} onClick={() => setComposer("note")}>Internal note</button>
              </div>
              {composer === "reply" ? <form onSubmit={sendReply}>
                <label>Reply to {detail.requester.name}<textarea aria-label="Public reply" value={reply} onChange={event => setReply(event.target.value)} rows={6} required /></label>
                <div className="composer-actions"><label>After sending<select value={replyStatus} onChange={event => setReplyStatus(event.target.value as TicketStatus)}><option value="in_progress">Keep in progress</option><option value="waiting_for_customer">Wait for customer</option><option value="resolved">Resolve</option></select></label><button disabled={!reply.trim()}>Send reply</button></div>
              </form> : <form onSubmit={addNote}>
                <label>Private support note<textarea aria-label="Internal note" value={note} onChange={event => setNote(event.target.value)} rows={5} required /></label>
                <div className="composer-actions"><span className="muted">Only support users and their agents can see this.</span><button className="note-button" disabled={!note.trim()}>Add note</button></div>
              </form>}
            </div>
          </div>
          <aside className="ticket-sidebar">
            <form className="ticket-fields" onSubmit={saveFields}>
              <p className="eyebrow">Ticket fields</p>
              <label>Status<select value={fieldDraft?.status ?? detail.summary.status} onChange={event => setFieldDraft(current => current && { ...current, status: event.target.value as TicketStatus })}>
                {queues.filter(item => item.value !== "mine").map(item => <option key={item.value} value={item.value}>{item.label}</option>)}
              </select></label>
              <label>Priority<select value={fieldDraft?.priority ?? detail.summary.priority} onChange={event => setFieldDraft(current => current && { ...current, priority: event.target.value as TicketPriority })}><option value="normal">Normal</option><option value="urgent">Urgent</option></select></label>
              <label>Assignee<select value={fieldDraft?.assigneeId ?? ""} onChange={event => setFieldDraft(current => current && { ...current, assigneeId: event.target.value })}><option value="">Unassigned</option>{ticketAssignees.map(person => <option key={person.id} value={person.id}>{person.name}{person.id === user.id ? " (you)" : ""}</option>)}</select></label>
              <button type="submit" disabled={!fieldsChanged}>Save ticket fields</button>
            </form>
            <div className="requester-card"><p className="eyebrow">Requester</p><strong>{detail.requester.name}</strong><a href={`mailto:${detail.requester.email}`}>{detail.requester.email}</a><span>{detail.requester.external_user_id}</span></div>
            <details className="instructions" open>
              <summary>Support instructions</summary>
              <pre>{detail.support_instructions || "No project-specific instructions have been added."}</pre>
            </details>
          </aside>
        </div>
      </>}
    </section>
  </div>;
}

function fieldsFrom(summary: TicketSummary): FieldDraft {
  return { status: summary.status, priority: summary.priority, assigneeId: summary.assignee?.id ?? "" };
}

function customerActivity(ticket: TicketSummary) {
  return new Date(ticket.last_customer_reply_at).getTime() >= new Date(ticket.updated_at).getTime();
}

function kindLabel(kind: ConversationEntry["kind"]) {
  return ({
    customer_message: "Customer message",
    public_reply: "Public reply",
    internal_note: "Internal note",
    system_event: "System event",
  } as const)[kind];
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat("en", { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}

function relativeTime(value: string) {
  const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1000);
  const formatter = new Intl.RelativeTimeFormat("en", { numeric: "auto" });
  if (Math.abs(seconds) < 60) return formatter.format(seconds, "second");
  const minutes = Math.round(seconds / 60);
  if (Math.abs(minutes) < 60) return formatter.format(minutes, "minute");
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 24) return formatter.format(hours, "hour");
  return formatter.format(Math.round(hours / 24), "day");
}
