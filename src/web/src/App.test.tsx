import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { App } from "./App";
import { SupportWorkspace } from "./SupportWorkspace";
import { EmailAdministration } from "./EmailAdministration";
import { SolutionsWorkspace } from "./SolutionsWorkspace";

const user = { id: "support-1", email: "support@example.test", name: "Support", role: "support" as const };
const project = { id: "project-1", key: "DOCS", name: "Documentation" };
const now = "2026-09-16T12:00:00Z";
const summary = {
  id: "ticket-1",
  number: "HLP-42",
  project,
  subject: "Settings page is blank",
  status: "open",
  priority: "urgent",
  version: 4,
  assignee: null,
  created_at: now,
  updated_at: now,
  last_customer_reply_at: now,
  waiting_since: now,
  snoozed_until: null,
} as const;
const detail = {
  summary,
  requester: { external_user_id: "customer-42", name: "Avery Customer", email: "avery@example.test" },
  context: { release: "2.4.1", page: "/settings" },
  support_instructions: "Ask for the release number before replying.",
  development_references: [],
  notifications: [],
  conversation: [
    { id: "entry-1", sequence: 1, kind: "customer_message", body: "Everything is blank.", is_public: true, created_at: now, actor: null,
      attachments: [{ id: "attachment-customer", file_name: "blank-screen.png", media_type: "image/png", size: 1536, is_public: true, created_at: now }] },
    { id: "entry-2", sequence: 2, kind: "public_reply", body: "We are checking.", is_public: true, created_at: now, actor: { user_name: "Support", agent_name: null },
      attachments: [{ id: "attachment-public", file_name: "steps.json", media_type: "application/json", size: 42, is_public: true, created_at: now }] },
    { id: "entry-3", sequence: 3, kind: "internal_note", body: "Reproduced.", is_public: false, created_at: now, actor: { user_name: "Support", agent_name: "Triage" },
      attachments: [{ id: "attachment-internal", file_name: "trace.txt", media_type: "text/plain", size: 2048, is_public: false, created_at: now }] },
    { id: "entry-4", sequence: 4, kind: "system_event", body: "Priority changed to Urgent.", is_public: false, created_at: now, actor: { user_name: "Support", agent_name: null }, attachments: [] },
  ],
};

it("shows a failed email delivery and queues a retry without a ticket version", async () => {
  const failed = {
    id: "notification-1", type: "public_reply_customer", target_kind: "customer", recipient_email: "avery@example.test",
    status: "failed", attempt_count: 4, next_attempt_at: null, submitted_at: null,
    last_error: "SMTP connection failed.", created_at: now,
  } as const;
  const fetch = stubSupportApi(async (path, init) => {
    if (path.endsWith("/tickets/HLP-42") && !init?.method) return response({ ...detail, notifications: [failed] });
    if (path.endsWith("/tickets/HLP-42/notifications/notification-1/retry") && init?.method === "POST") {
      return response({ ...failed, status: "pending", attempt_count: 0, last_error: null });
    }
    return null;
  });
  render(<SupportWorkspace user={user} projects={[project]} />);
  fireEvent.click(await screen.findByRole("button", { name: /HLP-42.*Settings page is blank/s }));
  expect(await screen.findByText("SMTP connection failed.")).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Retry delivery" }));

  await waitFor(() => expect(fetch).toHaveBeenCalledWith(
    "/api/backoffice/tickets/HLP-42/notifications/notification-1/retry",
    expect.objectContaining({
      method: "POST",
      headers: expect.objectContaining({ "Idempotency-Key": expect.any(String) }),
    }),
  ));
  const retryCall = fetch.mock.calls.find(([path]) => String(path).endsWith("/notifications/notification-1/retry"));
  expect(retryCall?.[1]?.headers).not.toHaveProperty("If-Match");
  expect(await screen.findByText("Email delivery has been queued for another attempt.")).toBeInTheDocument();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.localStorage.removeItem("helpaffe-theme");
  document.documentElement.classList.remove("dark");
});

it("offers sign in without a session", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(response(null, 401)));
  render(<App />);
  expect(await screen.findByRole("heading", { name: "Sign in to support." })).toBeInTheDocument();
});

it("shows the shared queues, filters, full context, and distinct conversation kinds", async () => {
  stubSupportApi();
  render(<SupportWorkspace user={user} projects={[project]} />);

  expect(await screen.findByRole("tab", { name: "Open" })).toHaveAttribute("aria-selected", "true");
  expect(screen.getByRole("tab", { name: "My tickets" })).toBeInTheDocument();
  expect(screen.getByRole("combobox", { name: "Project" })).toHaveTextContent("DOCS · Documentation");
  expect(screen.getByRole("combobox", { name: "Priority" })).toBeInTheDocument();
  expect(screen.getByRole("combobox", { name: "Assignee" })).toBeInTheDocument();

  fireEvent.click(await screen.findByRole("button", { name: /HLP-42.*Settings page is blank/s }));
  expect(await screen.findByRole("heading", { name: "Settings page is blank" })).toBeInTheDocument();
  expect(screen.getByText("Selected")).toBeInTheDocument();
  expect(screen.getByText("Customer message")).toBeInTheDocument();
  expect(screen.getAllByText("Public reply")).toHaveLength(2);
  expect(screen.getAllByText("Internal note")).toHaveLength(2);
  expect(screen.getByText("System event")).toBeInTheDocument();
  expect(screen.getByRole("link", { name: "Download blank-screen.png" })).toHaveAttribute(
    "href", "/api/backoffice/tickets/HLP-42/attachments/attachment-customer");
  expect(screen.getByRole("link", { name: "Download trace.txt" })).toHaveAttribute(
    "href", "/api/backoffice/tickets/HLP-42/attachments/attachment-internal");
  expect(screen.getAllByText("Customer-visible")).toHaveLength(2);
  expect(screen.getByText("Internal only")).toBeInTheDocument();
  expect(screen.getByText("image/png · 1.5 KiB")).toBeInTheDocument();
  expect(screen.getByText("Ask for the release number before replying.")).toBeInTheDocument();
  expect(screen.getByText(/"release": "2.4.1"/)).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: /HLP-41.*Earlier settings issue/s }));
  expect(await screen.findByRole("heading", { name: "Earlier settings issue" })).toBeInTheDocument();
  expect(screen.getAllByText("New customer activity")).toHaveLength(2);
});

it("moves between work queues with arrow keys", async () => {
  stubSupportApi();
  render(<SupportWorkspace user={user} projects={[project]} />);
  const open = await screen.findByRole("tab", { name: "Open" });
  open.focus();
  fireEvent.keyDown(open, { key: "ArrowRight" });
  expect(screen.getByRole("tab", { name: "In progress" })).toHaveAttribute("aria-selected", "true");
  expect(screen.getByRole("tab", { name: "In progress" })).toHaveFocus();
});

it("combines full-text search with project and status filters", async () => {
  const fetch = stubSupportApi();
  render(<SupportWorkspace user={user} projects={[project]} />);
  await screen.findByRole("tab", { name: "Open" });
  fireEvent.change(screen.getByRole("combobox", { name: "Project" }), { target: { value: project.id } });
  fireEvent.click(screen.getByRole("tab", { name: "Resolved" }));
  fireEvent.change(screen.getByPlaceholderText("Number, subject, requester…"), { target: { value: "blank page" } });
  fireEvent.click(screen.getByRole("button", { name: "Search" }));

  await waitFor(() => expect(fetch.mock.calls.some(([path]) =>
    String(path).includes("/tickets?status=resolved&project_id=project-1&search=blank+page"),
  )).toBe(true));
});

it("sends a public reply and selected files as multipart with concurrency protection", async () => {
  const fetch = stubSupportApi(async (path, init) => {
    if (path.endsWith("/tickets/HLP-42/replies") && init?.method === "POST") {
      return response({ ...detail, summary: { ...summary, status: "waiting_for_customer", version: 5 } });
    }
    return null;
  });
  render(<SupportWorkspace user={user} projects={[project]} />);
  fireEvent.click(await screen.findByRole("button", { name: /HLP-42.*Settings page is blank/s }));
  fireEvent.change(await screen.findByRole("textbox", { name: "Public reply" }), { target: { value: "Please try again now." } });
  const evidence = new File(["browser evidence"], "evidence.txt", { type: "text/plain", lastModified: 1 });
  fireEvent.change(screen.getByLabelText("Attach files to public reply"), { target: { files: [evidence] } });
  expect(screen.getByRole("list", { name: "Selected public attachments" })).toHaveTextContent("evidence.txt");
  fireEvent.click(screen.getByRole("button", { name: "Send reply" }));

  await waitFor(() => expect(fetch).toHaveBeenCalledWith(
    "/api/backoffice/tickets/HLP-42/replies",
    expect.objectContaining({
      method: "POST",
      headers: expect.objectContaining({ "If-Match": '"4"', "Idempotency-Key": expect.any(String) }),
    }),
  ));
  const replyCall = fetch.mock.calls.find(([path]) => String(path).endsWith("/tickets/HLP-42/replies"));
  const body = replyCall?.[1]?.body as FormData;
  expect(body).toBeInstanceOf(FormData);
  expect(body.get("message")).toBe("Please try again now.");
  expect(body.get("status")).toBe("waiting_for_customer");
  expect(body.getAll("files")).toEqual([evidence]);
  expect(replyCall?.[1]?.headers).not.toHaveProperty("Content-Type");
  expect(await screen.findByRole("textbox", { name: "Public reply" })).toHaveValue("");
  expect(screen.queryByRole("list", { name: "Selected public attachments" })).not.toBeInTheDocument();
  expect(screen.getByText("Public reply sent with 1 attachment.")).toBeInTheDocument();
});

it("shows upload state and keeps invalid selections from being submitted", async () => {
  let finishReply: ((value: ReturnType<typeof response>) => void) | undefined;
  const fetch = stubSupportApi(async (path, init) => {
    if (path.endsWith("/tickets/HLP-42/replies") && init?.method === "POST") {
      return await new Promise<ReturnType<typeof response>>(resolve => { finishReply = resolve; });
    }
    return null;
  });
  render(<SupportWorkspace user={user} projects={[project]} />);
  fireEvent.click(await screen.findByRole("button", { name: /HLP-42.*Settings page is blank/s }));
  fireEvent.change(await screen.findByRole("textbox", { name: "Public reply" }), { target: { value: "Uploading now." } });
  const evidence = new File(["evidence"], "evidence.txt", { type: "text/plain" });
  fireEvent.change(screen.getByLabelText("Attach files to public reply"), { target: { files: [evidence] } });
  fireEvent.click(screen.getByRole("button", { name: "Send reply" }));
  expect(await screen.findByRole("button", { name: "Sending…" })).toBeDisabled();
  expect(screen.getByText("Uploading public reply with 1 attachment…")).toBeInTheDocument();
  finishReply?.(response({ ...detail, summary: { ...summary, version: 5 } }));
  expect(await screen.findByText("Public reply sent with 1 attachment.")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Send reply" })).toBeDisabled();

  const tooMany = Array.from({ length: 6 }, (_, index) => new File(["x"], `file-${index}.txt`, { type: "text/plain" }));
  fireEvent.change(screen.getByRole("textbox", { name: "Public reply" }), { target: { value: "Do not send this." } });
  fireEvent.change(screen.getByLabelText("Attach files to public reply"), { target: { files: tooMany } });
  expect(screen.getByRole("alert")).toHaveTextContent("Choose at most 5 files.");
  expect(screen.getByRole("button", { name: "Send reply" })).toBeDisabled();
  expect(fetch.mock.calls.filter(([path]) => String(path).endsWith("/tickets/HLP-42/replies"))).toHaveLength(1);
});

it("uploads internal-note files only through the private note composer", async () => {
  const fetch = stubSupportApi(async (path, init) => {
    if (path.endsWith("/tickets/HLP-42/notes") && init?.method === "POST") {
      return response({ ...detail, summary: { ...summary, version: 5 } });
    }
    return null;
  });
  render(<SupportWorkspace user={user} projects={[project]} />);
  fireEvent.click(await screen.findByRole("button", { name: /HLP-42.*Settings page is blank/s }));
  await screen.findByRole("heading", { name: "Settings page is blank" });
  fireEvent.click(screen.getByRole("tab", { name: "Internal note" }));
  fireEvent.change(screen.getByRole("textbox", { name: "Internal note" }), { target: { value: "Private investigation." } });
  const trace = new File(["trace"], "trace.txt", { type: "text/plain" });
  fireEvent.change(screen.getByLabelText("Attach files to internal note"), { target: { files: [trace] } });
  expect(screen.getByText(/Only support users and their agents can see these files/)).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Add note" }));

  await waitFor(() => expect(fetch.mock.calls.some(([path]) => String(path).endsWith("/tickets/HLP-42/notes"))).toBe(true));
  const noteCall = fetch.mock.calls.find(([path]) => String(path).endsWith("/tickets/HLP-42/notes"));
  const body = noteCall?.[1]?.body as FormData;
  expect(body.get("message")).toBe("Private investigation.");
  expect(body.has("status")).toBe(false);
  expect(body.getAll("files")).toEqual([trace]);
});

it("snoozes and clears a ticket with version and idempotency protection", async () => {
  const fetch = stubSupportApi(async (path, init) => {
    if (path.endsWith("/tickets/HLP-42/snooze") && init?.method === "PUT") {
      const body = JSON.parse(String(init.body));
      return response({ ...detail, summary: { ...summary, version: 5, snoozed_until: body.snoozed_until ?? null } });
    }
    return null;
  });
  render(<SupportWorkspace user={user} projects={[project]} />);
  fireEvent.click(await screen.findByRole("button", { name: /HLP-42.*Settings page is blank/s }));
  fireEvent.change(await screen.findByLabelText("Return to queue"), { target: { value: new Date(Date.now() + 86_400_000).toISOString().slice(0, 16) } });
  fireEvent.click(screen.getByRole("button", { name: "Snooze ticket" }));

  await waitFor(() => expect(fetch).toHaveBeenCalledWith(
    "/api/backoffice/tickets/HLP-42/snooze",
    expect.objectContaining({
      method: "PUT",
      headers: expect.objectContaining({ "If-Match": '"4"', "Idempotency-Key": expect.any(String) }),
    }),
  ));
  expect(await screen.findByRole("button", { name: "Clear snooze" })).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Clear snooze" }));
  await waitFor(() => expect(fetch.mock.calls.filter(([path]) => String(path).endsWith("/snooze"))).toHaveLength(2));
  const clearCall = fetch.mock.calls.filter(([path]) => String(path).endsWith("/snooze"))[1];
  expect(clearCall[1]).toEqual(expect.objectContaining({ body: JSON.stringify({ snoozed_until: null }) }));
});

it("adds and removes development references with the current ticket version", async () => {
  const reference = { id: "reference-1", type: "github", url: "https://github.com/example/app/issues/42", label: "GH-42", position: 1, created_at: now } as const;
  const fetch = stubSupportApi(async (path, init) => {
    if (path.endsWith("/tickets/HLP-42/development-references") && init?.method === "POST") {
      return response({ ...detail, summary: { ...summary, version: 5 }, development_references: [reference] });
    }
    if (path.endsWith("/tickets/HLP-42/development-references/reference-1") && init?.method === "DELETE") {
      return response({ ...detail, summary: { ...summary, version: 6 }, development_references: [] });
    }
    return null;
  });
  render(<SupportWorkspace user={user} projects={[project]} />);
  fireEvent.click(await screen.findByRole("button", { name: /HLP-42.*Settings page is blank/s }));
  fireEvent.change(await screen.findByRole("textbox", { name: "Reference" }), { target: { value: "GH-42" } });
  fireEvent.change(screen.getByRole("textbox", { name: "HTTPS URL" }), { target: { value: reference.url } });
  fireEvent.click(screen.getByRole("button", { name: "Add reference" }));

  await waitFor(() => expect(fetch).toHaveBeenCalledWith(
    "/api/backoffice/tickets/HLP-42/development-references",
    expect.objectContaining({
      method: "POST",
      headers: expect.objectContaining({ "If-Match": '"4"', "Idempotency-Key": expect.any(String) }),
      body: JSON.stringify({ type: "github", url: reference.url, label: "GH-42" }),
    }),
  ));
  fireEvent.click(await screen.findByRole("button", { name: "Remove GH-42" }));
  await waitFor(() => expect(fetch).toHaveBeenCalledWith(
    "/api/backoffice/tickets/HLP-42/development-references/reference-1",
    expect.objectContaining({
      method: "DELETE",
      headers: expect.objectContaining({ "If-Match": '"5"', "Idempotency-Key": expect.any(String) }),
    }),
  ));
});

it("keeps a reply draft visible when the ticket version is stale", async () => {
  let detailReads = 0;
  stubSupportApi(async (path, init) => {
    if (path.endsWith("/tickets/HLP-42") && !init?.method) {
      detailReads += 1;
      return response(detailReads === 1 ? detail : { ...detail, summary: { ...summary, version: 5 } });
    }
    if (path.endsWith("/tickets/HLP-42/replies") && init?.method === "POST") {
      return response({ type: "/problems/stale", detail: "The ticket changed.", current_version: 5 }, 412);
    }
    return null;
  });
  render(<SupportWorkspace user={user} projects={[project]} />);
  fireEvent.click(await screen.findByRole("button", { name: /HLP-42.*Settings page is blank/s }));
  const reply = await screen.findByRole("textbox", { name: "Public reply" });
  fireEvent.change(reply, { target: { value: "Keep this carefully written draft." } });
  const evidence = new File(["keep me"], "keep-me.txt", { type: "text/plain" });
  fireEvent.change(screen.getByLabelText("Attach files to public reply"), { target: { files: [evidence] } });
  fireEvent.click(screen.getByRole("button", { name: "Send reply" }));

  expect(await screen.findByText("This ticket changed while you were working.")).toBeInTheDocument();
  expect(reply).toHaveValue("Keep this carefully written draft.");
  expect(screen.getByRole("list", { name: "Selected public attachments" })).toHaveTextContent("keep-me.txt");
  fireEvent.click(screen.getByRole("button", { name: "Reload ticket" }));
  await waitFor(() => expect(detailReads).toBe(2));
  expect(reply).toHaveValue("Keep this carefully written draft.");
  expect(screen.getByRole("list", { name: "Selected public attachments" })).toHaveTextContent("keep-me.txt");
});

it("searches, creates, and opens project solution articles", async () => {
  const solution = {
    id: "solution-1", project_id: project.id, key: "postgres-restart", title: "Restart PostgreSQL safely",
    markdown: "Use the tested restart playbook.", version: 1, created_at: now, updated_at: now,
  };
  const fetch = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
    const path = String(input);
    if (path.includes(`/projects/${project.id}/solutions?search=database`) && !init?.method) {
      return response({ items: [solution], next_cursor: null });
    }
    if (path.endsWith(`/projects/${project.id}/solutions`) && !init?.method) {
      return response({ items: [solution], next_cursor: null });
    }
    if (path.endsWith(`/projects/${project.id}/solutions/postgres-restart`) && !init?.method) {
      return response(solution);
    }
    if (path.endsWith(`/projects/${project.id}/solutions`) && init?.method === "POST") {
      return response({ ...solution, ...JSON.parse(String(init.body)), id: "solution-2", key: "cache-reset" }, 201);
    }
    throw new Error(`Unexpected request: ${init?.method ?? "GET"} ${path}`);
  });
  vi.stubGlobal("fetch", fetch);
  render(<SolutionsWorkspace projects={[project]} />);

  expect(await screen.findByRole("button", { name: /postgres-restart.*Restart PostgreSQL safely/s })).toBeInTheDocument();
  fireEvent.change(screen.getByPlaceholderText("Title or Markdown…"), { target: { value: "database" } });
  fireEvent.click(screen.getByRole("button", { name: "Search" }));
  await waitFor(() => expect(fetch.mock.calls.some(([path]) => String(path).includes("solutions?search=database"))).toBe(true));

  fireEvent.click(screen.getByRole("button", { name: "New article" }));
  fireEvent.change(screen.getByLabelText("Key"), { target: { value: "cache-reset" } });
  fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Reset the cache" } });
  fireEvent.change(screen.getByRole("textbox", { name: "Markdown" }), { target: { value: "Clear cached data safely." } });
  fireEvent.click(screen.getByRole("button", { name: "Create article" }));
  await waitFor(() => expect(fetch).toHaveBeenCalledWith(
    `/api/backoffice/projects/${project.id}/solutions`,
    expect.objectContaining({
      method: "POST",
      headers: expect.objectContaining({ "Idempotency-Key": expect.any(String) }),
      body: JSON.stringify({ key: "cache-reset", title: "Reset the cache", markdown: "Clear cached data safely." }),
    }),
  ));
  expect(await screen.findByRole("heading", { name: "Reset the cache" })).toBeInTheDocument();
});

it("preserves a solution draft across a stale conflict and confirms deletion", async () => {
  const solution = {
    id: "solution-1", project_id: project.id, key: "postgres-restart", title: "Restart PostgreSQL safely",
    markdown: "Original runbook.", version: 1, created_at: now, updated_at: now,
  };
  let detailReads = 0;
  let updates = 0;
  let deleted = false;
  const fetch = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
    const path = String(input);
    if (path.endsWith(`/projects/${project.id}/solutions`) && !init?.method) {
      return response({ items: deleted ? [] : [solution], next_cursor: null });
    }
    if (path.endsWith(`/projects/${project.id}/solutions/postgres-restart`) && !init?.method) {
      detailReads += 1;
      return response({ ...solution, version: detailReads === 1 ? 1 : 2, markdown: detailReads === 1 ? solution.markdown : "Someone else's runbook." });
    }
    if (path.endsWith(`/projects/${project.id}/solutions/postgres-restart`) && init?.method === "PUT") {
      updates += 1;
      if (updates === 1) return response({ type: "/problems/stale", detail: "The solution article changed.", current_version: 2 }, 412);
      expect(init.headers).toEqual(expect.objectContaining({ "If-Match": '"2"' }));
      return response({ ...solution, ...JSON.parse(String(init.body)), version: 3 });
    }
    if (path.endsWith(`/projects/${project.id}/solutions/postgres-restart`) && init?.method === "DELETE") {
      expect(init.headers).toEqual(expect.objectContaining({ "If-Match": '"3"' }));
      deleted = true;
      return response(null, 204);
    }
    throw new Error(`Unexpected request: ${init?.method ?? "GET"} ${path}`);
  });
  vi.stubGlobal("fetch", fetch);
  render(<SolutionsWorkspace projects={[project]} />);

  fireEvent.click(await screen.findByRole("button", { name: /postgres-restart.*Restart PostgreSQL safely/s }));
  expect(await screen.findByRole("heading", { name: "Restart PostgreSQL safely" })).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Edit article" }));
  const markdown = screen.getByRole("textbox", { name: "Markdown" });
  fireEvent.change(markdown, { target: { value: "Carefully revised runbook." } });
  fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
  expect(await screen.findByText("This article changed while you were editing.")).toBeInTheDocument();
  expect(markdown).toHaveValue("Carefully revised runbook.");

  fireEvent.click(screen.getByRole("button", { name: "Reload article" }));
  await waitFor(() => expect(detailReads).toBe(2));
  expect(screen.getByRole("textbox", { name: "Markdown" })).toHaveValue("Carefully revised runbook.");
  fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
  expect(await screen.findByText("Solution article updated.")).toBeInTheDocument();

  fireEvent.click(screen.getByRole("button", { name: "Delete article" }));
  expect(screen.getByText("Delete this solution article?")).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Delete permanently" }));
  expect(await screen.findByText("Solution article deleted.")).toBeInTheDocument();
  expect(await screen.findByText("No solutions yet.")).toBeInTheDocument();
});

it("keeps human administration behind its navigation entry", async () => {
  const fetch = vi.fn(async (input: string | URL | Request) => {
    const path = String(input);
    if (path.endsWith("/me")) return response({ id: "admin", email: "admin@example.test", name: "Admin", role: "administrator" });
    if (path.endsWith("/projects")) return response([project]);
    if (path.includes("/assignees")) return response([]);
    if (path.includes("/tickets?")) return response({ items: [], next_cursor: null });
    if (path.endsWith("/agents")) return response([]);
    if (path.endsWith("/product-keys")) return response([]);
    if (path.endsWith("/users")) return response([{ id: "support-1", email: "support@example.test", name: "Support", role: "support", is_active: true, project_ids: [] }]);
    throw new Error(`Unexpected request: ${path}`);
  });
  vi.stubGlobal("fetch", fetch);
  render(<App />);

  fireEvent.click(await screen.findByRole("button", { name: "Administration" }));
  expect(await screen.findByRole("heading", { name: "People" })).toBeInTheDocument();
  expect(screen.getByRole("combobox", { name: "Role for Support" })).toHaveValue("support");
});

it("follows system appearance and saves a selected theme", async () => {
  const listeners = new Set<() => void>();
  const preference = {
    matches: true,
    addEventListener: (_event: string, listener: () => void) => listeners.add(listener),
    removeEventListener: (_event: string, listener: () => void) => listeners.delete(listener),
  };
  vi.stubGlobal("matchMedia", () => preference);
  vi.stubGlobal("fetch", vi.fn(async (input: string | URL | Request) => {
    const path = String(input);
    if (path.endsWith("/me")) return response(user);
    if (path.endsWith("/projects")) return response([project]);
    if (path.includes("/assignees")) return response([]);
    if (path.includes("/tickets?")) return response({ items: [], next_cursor: null });
    throw new Error(`Unexpected request: ${path}`);
  }));
  render(<App />);

  const appearance = await screen.findByRole("combobox", { name: "Appearance" });
  expect(document.documentElement).toHaveClass("dark");
  preference.matches = false;
  listeners.forEach(listener => listener());
  expect(document.documentElement).not.toHaveClass("dark");
  fireEvent.change(appearance, { target: { value: "dark" } });
  expect(document.documentElement).toHaveClass("dark");
  expect(window.localStorage.getItem("helpaffe-theme")).toBe("dark");
  fireEvent.change(appearance, { target: { value: "light" } });
  expect(document.documentElement).not.toHaveClass("dark");
  expect(window.localStorage.getItem("helpaffe-theme")).toBe("light");
  fireEvent.click(screen.getByRole("button", { name: "Open navigation" }));
  expect(document.querySelector('button[aria-controls="app-navigation"]')).toHaveAttribute("aria-expanded", "true");
  fireEvent.keyDown(window, { key: "Escape" });
  expect(screen.getByRole("button", { name: "Open navigation" })).toHaveAttribute("aria-expanded", "false");
});

it("manages project email settings, templates, previews, and test delivery", async () => {
  const settings = {
    project_id: project.id,
    project_name: project.name,
    language: "en",
    smtp: { host: "smtp.example.test", port: 587, use_tls: true, username: "smtp-user", password_configured: true },
    sender: { name: "Documentation", email: "support@example.test" },
    support_recipients: ["team@example.test"],
    branding: { name: "Docs", logo_url: null, color: "#336699" },
    ticket_links: { customer: "https://docs.example/support/{{ticket_number}}", backoffice: "https://support.example/tickets/{{ticket_number}}" },
  };
  const template = {
    type: "public_reply_customer",
    description: "Customer reply template",
    subject: "Reply {{ticket_number}}",
    text_body: "Hello {{customer_name}}",
    html_body: "<p>Hello {{customer_name}}</p>",
    variables: ["customer_name", "ticket_number"],
    is_customized: false,
  };
  const fetch = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
    const path = String(input);
    if (path.endsWith(`/projects/${project.id}/email-settings`)) return response(settings);
    if (path.endsWith(`/projects/${project.id}/email-templates`) && !init?.method) return response([template]);
    if (path.endsWith("/email-templates/public_reply_customer") && init?.method === "PUT") return response({ ...template, is_customized: true, ...JSON.parse(String(init.body)) });
    if (path.endsWith("/email-templates/public_reply_customer/preview")) return response({ subject: "Reply HLP-42", text_body: "Hello Avery", html_body: "<p>Hello Avery</p>" });
    if (path.endsWith("/email/test")) return response({ status: "submitted_to_smtp" });
    throw new Error(`Unexpected request: ${init?.method ?? "GET"} ${path}`);
  });
  vi.stubGlobal("fetch", fetch);
  render(<EmailAdministration projects={[project]} />);

  expect(await screen.findByDisplayValue("smtp.example.test")).toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("SMTP password"), { target: { value: "replacement-secret" } });
  fireEvent.click(screen.getByRole("button", { name: "Save email settings" }));
  await waitFor(() => expect(fetch).toHaveBeenCalledWith(
    `/api/backoffice/projects/${project.id}/email-settings`,
    expect.objectContaining({ method: "PUT", body: expect.stringContaining("replacement-secret") }),
  ));

  fireEvent.change(await screen.findByLabelText("Subject"), { target: { value: "Updated {{ticket_number}}" } });
  fireEvent.click(screen.getByRole("button", { name: "Save template" }));
  await waitFor(() => expect(fetch).toHaveBeenCalledWith(
    `/api/backoffice/projects/${project.id}/email-templates/public_reply_customer`,
    expect.objectContaining({ method: "PUT" }),
  ));
  fireEvent.click(screen.getByRole("button", { name: "Preview" }));
  expect(await screen.findByRole("heading", { name: "Reply HLP-42" })).toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("Test recipient"), { target: { value: "admin@example.test" } });
  fireEvent.click(screen.getByRole("button", { name: "Send test email" }));
  expect(await screen.findByText("Test email submitted to SMTP for admin@example.test.")).toBeInTheDocument();
});

function stubSupportApi(
  override?: (path: string, init?: RequestInit) => Promise<ReturnType<typeof response> | null>,
) {
  const fetch = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
    const path = String(input);
    const overridden = await override?.(path, init);
    if (overridden) return overridden;
    if (path.includes("/assignees")) return response([{ id: "support-1", name: "Support" }]);
    if (path.includes("/tickets?") && !init?.method) return response({ items: [summary], next_cursor: null });
    if (path.endsWith("/tickets/HLP-42/requester-tickets") && !init?.method) return response({ items: [{ ...summary, id: "ticket-previous", number: "HLP-41", subject: "Earlier settings issue", status: "resolved" }], next_cursor: null });
    if (path.endsWith("/tickets/HLP-41/requester-tickets") && !init?.method) return response({ items: [summary], next_cursor: null });
    if (path.endsWith("/tickets/HLP-41") && !init?.method) return response({ ...detail, summary: { ...summary, id: "ticket-previous", number: "HLP-41", subject: "Earlier settings issue", status: "resolved" } });
    if (path.endsWith("/tickets/HLP-42") && !init?.method) return response(detail);
    throw new Error(`Unexpected request: ${init?.method ?? "GET"} ${path}`);
  });
  vi.stubGlobal("fetch", fetch);
  return fetch;
}

function response(body: unknown, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  };
}
