import "@testing-library/jest-dom/vitest";

// Node's experimental localStorage can mask jsdom's storage in the test runner.
const stored = new Map<string, string>();
Object.defineProperty(window, "localStorage", {
  configurable: true,
  value: {
    getItem: (key: string) => stored.get(key) ?? null,
    setItem: (key: string, value: string) => { stored.set(key, value); },
    removeItem: (key: string) => { stored.delete(key); },
    clear: () => { stored.clear(); },
  },
});
