import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";
import { App } from "./App";

it("renders the application foundation", () => {
  render(<App />);
  expect(screen.getByRole("heading", { name: "Helpdesk made easy and agentic." })).toBeInTheDocument();
});
