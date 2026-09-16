export type Role = "administrator" | "support";
export type CurrentUser = { id: string; email: string; name: string; role: Role };
export type Project = { id: string; key: string; name: string };

type Problem = {
  type?: string;
  detail?: string;
  current_version?: number;
};

export class RequestError extends Error {
  constructor(
    public readonly status: number,
    public readonly type: string,
    detail: string,
    public readonly currentVersion?: number,
  ) {
    super(detail);
  }
}

export async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api/backoffice${path}`, {
    credentials: "same-origin",
    ...init,
    headers: {
      ...(init?.method && init.method !== "GET"
        ? { "X-Helpaffe-CSRF": "1", "Content-Type": "application/json" }
        : {}),
      ...init?.headers,
    },
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as Problem | null;
    throw new RequestError(
      response.status,
      problem?.type ?? "/problems/request-failed",
      problem?.detail ?? (response.status === 401
        ? "The email address or password is incorrect."
        : "The request failed."),
      problem?.current_version,
    );
  }
  return (response.status === 204 ? undefined : await response.json()) as T;
}

export function requestKey() {
  return globalThis.crypto.randomUUID();
}

export function message(reason: unknown) {
  return reason instanceof Error ? reason.message : "The request failed.";
}
