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
  snoozed_until: string | null;
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
type NotificationDelivery = {
  id: string;
  type: string;
  target_kind: string;
  recipient_email: string | null;
  status: "pending" | "submitted_to_smtp" | "failed";
  attempt_count: number;
  next_attempt_at: string | null;
  submitted_at: string | null;
  last_error: string | null;
  created_at: string;
};
type DevelopmentReference = {
  id: string;
  type: "planaffe" | "github" | "gitlab";
  url: string;
  label: string;
  position: number;
  created_at: string;
};
type TicketDetail = {
  summary: TicketSummary;
  requester: { external_user_id: string; name: string; email: string };
  context: Record<string, unknown> | null;
  support_instructions: string;
  development_references: DevelopmentReference[];
  notifications: NotificationDelivery[];
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
  const [requesterTickets, setRequesterTickets] = useState<TicketSummary[]>([]);
  const [requesterTicketsCursor, setRequesterTicketsCursor] = useState<string | null>(null);
  const [fieldDraft, setFieldDraft] = useState<FieldDraft | null>(null);
  const [reply, setReply] = useState("");
  const [replyStatus, setReplyStatus] = useState<TicketStatus>("waiting_for_customer");
  const [note, setNote] = useState("");
  const [snoozeUntil, setSnoozeUntil] = useState("");
  const [referenceDraft, setReferenceDraft] = useState({ type: "github", url: "", label: "" });
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
      setSnoozeUntil(toLocalDateTime(selected.summary.snoozed_until));
      setRequesterTickets([]);
      setRequesterTicketsCursor(null);
      if (!preserveDrafts) {
        setFieldDraft(fieldsFrom(selected.summary));
        setReply("");
        setNote("");
      }
      setConflict(null);
      void loadAssignees(selected.summary.project.id, setTicketAssignees);
      void loadRequesterTickets(selected.summary.number);
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function loadRequesterTickets(number: string, cursor?: string) {
    try {
      const query = cursor ? `?cursor=${encodeURIComponent(cursor)}` : "";
      const page = await call<TicketPage>(`/tickets/${encodeURIComponent(number)}/requester-tickets${query}`);
      setRequesterTickets(current => cursor ? [...current, ...page.items] : page.items);
      setRequesterTicketsCursor(page.next_cursor);
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
      setRequesterTickets([]);
      setRequesterTicketsCursor(null);
      setFieldDraft(fieldsFrom(acquired.summary));
      setConflict(null);
      setNotice(`${acquired.summary.number} is now assigned to you.`);
      void loadAssignees(acquired.summary.project.id, setTicketAssignees);
      void loadRequesterTickets(acquired.summary.number);
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
      setSnoozeUntil(toLocalDateTime(updated.summary.snoozed_until));
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

  async function retryNotification(notificationId: string) {
    if (!detail) return;
    try {
      setError("");
      setNotice("");
      await call(`/tickets/${encodeURIComponent(detail.summary.number)}/notifications/${encodeURIComponent(notificationId)}/retry`, {
        method: "POST",
        headers: { "Idempotency-Key": requestKey() },
      });
      setNotice("Email delivery has been queued for another attempt.");
      await openTicket(detail.summary.number, true);
    } catch (reason) {
      setError(message(reason));
    }
  }

  async function saveSnooze(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!detail || !snoozeUntil) return;
    const parsed = new Date(snoozeUntil);
    if (Number.isNaN(parsed.getTime())) {
      setError("Choose a valid snooze date and time.");
      return;
    }
    await applyMutation(() => call<TicketDetail>(`/tickets/${encodeURIComponent(detail.summary.number)}/snooze`, {
      method: "PUT",
      headers: { "If-Match": `"${detail.summary.version}"`, "Idempotency-Key": requestKey() },
      body: JSON.stringify({ snoozed_until: parsed.toISOString() }),
    }));
  }

  async function clearSnooze() {
    if (!detail) return;
    await applyMutation(() => call<TicketDetail>(`/tickets/${encodeURIComponent(detail.summary.number)}/snooze`, {
      method: "PUT",
      headers: { "If-Match": `"${detail.summary.version}"`, "Idempotency-Key": requestKey() },
      body: JSON.stringify({ snoozed_until: null }),
    }));
  }

  async function addDevelopmentReference(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!detail || !referenceDraft.url.trim() || !referenceDraft.label.trim()) return;
    await applyMutation(() => call<TicketDetail>(`/tickets/${encodeURIComponent(detail.summary.number)}/development-references`, {
      method: "POST",
      headers: { "If-Match": `"${detail.summary.version}"`, "Idempotency-Key": requestKey() },
      body: JSON.stringify(referenceDraft),
    }), () => setReferenceDraft(current => ({ ...current, url: "", label: "" })));
  }

  async function removeDevelopmentReference(referenceId: string) {
    if (!detail) return;
    await applyMutation(() => call<TicketDetail>(`/tickets/${encodeURIComponent(detail.summary.number)}/development-references/${encodeURIComponent(referenceId)}`, {
      method: "DELETE",
      headers: { "If-Match": `"${detail.summary.version}"`, "Idempotency-Key": requestKey() },
    }));
  }

  function selectQueue(value: Queue) {
    setQueue(value);
    setDetail(null);
    setRequesterTickets([]);
    setRequesterTicketsCursor(null);
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
        {detail.summary.snoozed_until && <div className="notice" role="status">Snoozed until {formatDate(detail.summary.snoozed_until)}. Direct work remains available.</div>}
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
            <form className="snooze-card" onSubmit={saveSnooze}>
              <p className="eyebrow">Snooze</p>
              <label>Return to queue<input type="datetime-local" value={snoozeUntil} min={toLocalDateTime(new Date(Date.now() + 60_000).toISOString())} onChange={event => setSnoozeUntil(event.target.value)} required /></label>
              <button type="submit">Snooze ticket</button>
              {detail.summary.snoozed_until && <button type="button" className="secondary" onClick={() => void clearSnooze()}>Clear snooze</button>}
            </form>
            <section className="development-references" aria-label="Development references">
              <p className="eyebrow">Development references</p>
              {detail.development_references.length === 0 ? <span className="muted">No linked development tasks.</span> : <ol>
                {detail.development_references.map(reference => <li key={reference.id}>
                  <a href={reference.url} target="_blank" rel="noreferrer"><strong>{reference.label}</strong><span>{reference.type}</span></a>
                  <button type="button" className="secondary compact" onClick={() => void removeDevelopmentReference(reference.id)}>Remove {reference.label}</button>
                </li>)}
              </ol>}
              <form className="reference-form" onSubmit={addDevelopmentReference}>
                <label>Task system<select value={referenceDraft.type} onChange={event => setReferenceDraft(current => ({ ...current, type: event.target.value }))}><option value="planaffe">Planaffe</option><option value="github">GitHub</option><option value="gitlab">GitLab</option></select></label>
                <label>Reference<input value={referenceDraft.label} maxLength={200} onChange={event => setReferenceDraft(current => ({ ...current, label: event.target.value }))} required /></label>
                <label>HTTPS URL<input type="url" pattern="https://.*" value={referenceDraft.url} maxLength={2048} onChange={event => setReferenceDraft(current => ({ ...current, url: event.target.value }))} required /></label>
                <button type="submit">Add reference</button>
              </form>
            </section>
            <div className="requester-card"><p className="eyebrow">Requester</p><strong>{detail.requester.name}</strong><a href={`mailto:${detail.requester.email}`}>{detail.requester.email}</a><span>{detail.requester.external_user_id}</span></div>
            <section className="related-tickets" aria-label="Other tickets from this requester">
              <p className="eyebrow">Other requester tickets</p>
              {requesterTickets.length === 0 ? <span className="muted">No other tickets in this project.</span> : <ol>
                {requesterTickets.map(ticket => <li key={ticket.id}><button className="secondary" onClick={() => void openTicket(ticket.number)}>
                  <span><strong>{ticket.number}</strong><span>{ticket.status.replaceAll("_", " ")}</span></span>
                  <span>{ticket.subject}</span>
                </button></li>)}
              </ol>}
              {requesterTicketsCursor && <button className="secondary compact" onClick={() => void loadRequesterTickets(detail.summary.number, requesterTicketsCursor)}>Load more</button>}
            </section>
            <details className="ticket-context" open={detail.context !== null}>
              <summary>Technical context</summary>
              {detail.context === null ? <span className="muted">No technical context was supplied.</span> : <pre>{JSON.stringify(detail.context, null, 2)}</pre>}
            </details>
            {detail.notifications.length > 0 && <section className="notification-card" aria-label="Email deliveries">
              <p className="eyebrow">Email deliveries</p>
              <ol>
                {detail.notifications.map(notification => <li className={notification.status} key={notification.id}>
                  <div><strong>{notificationLabel(notification.type)}</strong><span>{notification.recipient_email ?? targetLabel(notification.target_kind)}</span></div>
                  <span className="delivery-status">{notification.status.replaceAll("_", " ")}</span>
                  {notification.last_error && <p>{notification.last_error}</p>}
                  {notification.status === "failed" && <button className="secondary compact" onClick={() => void retryNotification(notification.id)}>Retry delivery</button>}
                </li>)}
              </ol>
            </section>}
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

function notificationLabel(type: string) {
  return ({
    new_ticket_customer: "New ticket confirmation",
    new_ticket_support: "New ticket alert",
    customer_reply_support: "Customer reply alert",
    public_reply_customer: "Public reply",
    assignment_support: "Assignment alert",
  } as Record<string, string>)[type] ?? type.replaceAll("_", " ");
}

function targetLabel(target: string) {
  return ({ support_recipients: "Project support recipients", customer: "Customer", assignee: "Assignee" } as Record<string, string>)[target] ?? target;
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat("en", { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}

function toLocalDateTime(value: string | null) {
  if (!value) return "";
  const date = new Date(value);
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
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
