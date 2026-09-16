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
    throw new Error(`Unexpected request: ${path}`);
  }));

  render(<App />);

  expect(await screen.findByRole("heading", { name: "Projects" })).toBeInTheDocument();
  expect(screen.queryByRole("heading", { name: "People" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Add project" })).not.toBeInTheDocument();
});

function response(body: unknown, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  };
}
