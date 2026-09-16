import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { App } from "./App";
import { SupportWorkspace } from "./SupportWorkspace";

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
} as const;
const detail = {
  summary,
  requester: { external_user_id: "customer-42", name: "Avery Customer", email: "avery@example.test" },
  support_instructions: "Ask for the release number before replying.",
  conversation: [
    { id: "entry-1", sequence: 1, kind: "customer_message", body: "Everything is blank.", is_public: true, created_at: now, actor: null },
    { id: "entry-2", sequence: 2, kind: "public_reply", body: "We are checking.", is_public: true, created_at: now, actor: { user_name: "Support", agent_name: null } },
    { id: "entry-3", sequence: 3, kind: "internal_note", body: "Reproduced.", is_public: false, created_at: now, actor: { user_name: "Support", agent_name: "Triage" } },
    { id: "entry-4", sequence: 4, kind: "system_event", body: "Priority changed to Urgent.", is_public: false, created_at: now, actor: { user_name: "Support", agent_name: null } },
  ],
};

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
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
  expect(screen.getByText("Customer message")).toBeInTheDocument();
  expect(screen.getAllByText("Public reply")).toHaveLength(2);
  expect(screen.getAllByText("Internal note")).toHaveLength(2);
  expect(screen.getByText("System event")).toBeInTheDocument();
  expect(screen.getByText("Ask for the release number before replying.")).toBeInTheDocument();
  expect(screen.getAllByText("New customer activity")).toHaveLength(2);
});

it("sends a public reply with the last-read version and an idempotency key", async () => {
  const fetch = stubSupportApi(async (path, init) => {
    if (path.endsWith("/tickets/HLP-42/replies") && init?.method === "POST") {
      return response({ ...detail, summary: { ...summary, status: "waiting_for_customer", version: 5 } });
    }
    return null;
  });
  render(<SupportWorkspace user={user} projects={[project]} />);
  fireEvent.click(await screen.findByRole("button", { name: /HLP-42.*Settings page is blank/s }));
  fireEvent.change(await screen.findByRole("textbox", { name: "Public reply" }), { target: { value: "Please try again now." } });
  fireEvent.click(screen.getByRole("button", { name: "Send reply" }));

  await waitFor(() => expect(fetch).toHaveBeenCalledWith(
    "/api/backoffice/tickets/HLP-42/replies",
    expect.objectContaining({
      method: "POST",
      headers: expect.objectContaining({ "If-Match": '"4"', "Idempotency-Key": expect.any(String) }),
    }),
  ));
  expect(await screen.findByRole("textbox", { name: "Public reply" })).toHaveValue("");
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
  fireEvent.click(screen.getByRole("button", { name: "Send reply" }));

  expect(await screen.findByText("This ticket changed while you were working.")).toBeInTheDocument();
  expect(reply).toHaveValue("Keep this carefully written draft.");
  fireEvent.click(screen.getByRole("button", { name: "Reload ticket" }));
  await waitFor(() => expect(detailReads).toBe(2));
  expect(reply).toHaveValue("Keep this carefully written draft.");
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

function stubSupportApi(
  override?: (path: string, init?: RequestInit) => Promise<ReturnType<typeof response> | null>,
) {
  const fetch = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
    const path = String(input);
    const overridden = await override?.(path, init);
    if (overridden) return overridden;
    if (path.includes("/assignees")) return response([{ id: "support-1", name: "Support" }]);
    if (path.includes("/tickets?") && !init?.method) return response({ items: [summary], next_cursor: null });
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
