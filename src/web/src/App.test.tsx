import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { App } from "./App";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
it("offers sign in without a session", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue({ ok: false, status: 401 }));
  render(<App />);
  expect(await screen.findByRole("heading", { name: "Sign in to support." })).toBeInTheDocument();
});

it("lets an administrator manage roles and project access", async () => {
  const fetch = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
    const path = String(input);
    if (path.endsWith("/me")) return response({ id: "admin", email: "admin@example.test", name: "Admin", role: "administrator" });
    if (path.endsWith("/projects")) return response([{ id: "project-1", key: "DOCS", name: "Documentation" }]);
    if (path.endsWith("/agents")) return response([]);
    if (path.endsWith("/product-keys")) return response([]);
    if (path.endsWith("/users")) return response([{ id: "support-1", email: "support@example.test", name: "Support", role: "support", is_active: true, project_ids: [] }]);
    if (path.includes("/users/support-1/projects/project-1") && init?.method === "PUT") return response(undefined, 204);
    throw new Error(`Unexpected request: ${init?.method ?? "GET"} ${path}`);
  });
  vi.stubGlobal("fetch", fetch);

  render(<App />);

  expect(await screen.findByRole("heading", { name: "People" })).toBeInTheDocument();
  expect(await screen.findByRole("combobox", { name: "Role for Support" })).toHaveValue("support");
  fireEvent.click(screen.getByRole("checkbox", { name: "Documentation" }));
  await waitFor(() => expect(fetch).toHaveBeenCalledWith(
    "/api/backoffice/users/support-1/projects/project-1",
    expect.objectContaining({ method: "PUT" }),
  ));
});

it("does not expose administration to a support user", async () => {
  vi.stubGlobal("fetch", vi.fn(async (input: string | URL | Request) => {
    const path = String(input);
    if (path.endsWith("/me")) return response({ id: "support", email: "support@example.test", name: "Support", role: "support" });
    if (path.endsWith("/projects")) return response([]);
    if (path.endsWith("/agents")) return response([]);
    throw new Error(`Unexpected request: ${path}`);
  }));

  render(<App />);

  expect(await screen.findByRole("heading", { name: "Projects" })).toBeInTheDocument();
  expect(screen.queryByRole("heading", { name: "People" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Add project" })).not.toBeInTheDocument();
  expect(screen.getByRole("heading", { name: "Agent credentials" })).toBeInTheDocument();
  expect(screen.queryByRole("heading", { name: "Product API keys" })).not.toBeInTheDocument();
});

it("shows a newly created agent token once", async () => {
  const fetch = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
    const path = String(input);
    if (path.endsWith("/me")) return response({ id: "support", email: "support@example.test", name: "Support", role: "support" });
    if (path.endsWith("/projects")) return response([{ id: "project-1", key: "DOCS", name: "Documentation" }]);
    if (path.endsWith("/agents") && init?.method === "POST") return response({
      credential: { id: "agent-1", user_id: "support", user_name: "Support", name: "Triage", token_prefix: "hfa_example", all_projects: true, project_ids: [], is_active: true },
      token: "hfa_example-secret",
    }, 201);
    if (path.endsWith("/agents")) return response([]);
    throw new Error(`Unexpected request: ${init?.method ?? "GET"} ${path}`);
  });
  vi.stubGlobal("fetch", fetch);
  render(<App />);

  fireEvent.change(await screen.findByRole("textbox", { name: "Agent name" }), { target: { value: "Triage" } });
  fireEvent.click(screen.getByRole("button", { name: "Create agent token" }));

  expect(await screen.findByText("hfa_example-secret")).toBeInTheDocument();
  expect(fetch).toHaveBeenCalledWith("/api/backoffice/agents", expect.objectContaining({ method: "POST" }));
});

function response(body: unknown, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  };
}
