// Typed client for the NX Hub REST API. Shapes mirror the server's JSON (camelCase, enums as strings).

export type ConnectionState = "disconnected" | "connecting" | "connected" | "reconnecting" | "disconnecting";

export type Button =
  | "a"
  | "b"
  | "x"
  | "y"
  | "l"
  | "r"
  | "zl"
  | "zr"
  | "plus"
  | "minus"
  | "dpadUp"
  | "dpadDown"
  | "dpadLeft"
  | "dpadRight"
  | "home"
  | "capture"
  | "leftStick"
  | "rightStick";

export type Stick = "left" | "right";

export interface Capabilities {
  rawVersion: string;
  implementation: "unknown" | "sysBotBase" | "sysBotBaseCpp";
  isInferred: boolean;
  buttons: boolean;
  sticks: boolean;
  touch: boolean;
  screenshot: boolean;
  tickPreciseController: boolean;
  warnings: string[];
}

export interface ControllerSnapshot {
  heldButtons: Button[];
  sticks: Partial<Record<Stick, { x: number; y: number }>>;
  pendingNeutralise: boolean;
}

export interface SwitchStatus {
  switchId: string;
  state: ConnectionState;
  autoConnect: boolean;
  stateSince: string;
  connectedSince: string | null;
  reconnectAttempt: number;
  nextAttemptAt: string | null;
  lastAttemptAt: string | null;
  connectionCount: number;
  connectionLossCount: number;
  lastError: { message: string; code: string | null; at: string } | null;
  latency: { lastMs: number; averageMs: number; measuredAt: string } | null;
  capabilities: Capabilities | null;
  /** Set while another program (for example a trade bot) has the Switch. */
  lease: { holder: string; acquiredAt: string; expiresAt: string } | null;
  controller: ControllerSnapshot;
}

export interface SwitchInfo {
  id: string;
  name: string;
  host: string;
  port: number;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
  status: SwitchStatus;
}

export type CheckStatus = "pass" | "warn" | "fail" | "skipped" | "info";

export interface DiagnosticReport {
  switchId: string;
  startedAt: string;
  completedAt: string;
  overall: "ready" | "degraded" | "notReady";
  checks: { id: string; title: string; status: CheckStatus; message: string; durationMs: number }[];
}

export interface ActivityEntry {
  sequence: number;
  timestamp: string;
  level: "info" | "warning" | "error";
  category: string;
  message: string;
}

export interface ServerInfo {
  name: string;
  version: string;
  authRequired: boolean;
  switches: number;
  demo: boolean;
  theme: "dark" | "light" | "system";
}

export type MacroStepType = "press" | "hold" | "release" | "stick" | "wait" | "repeat";

export interface MacroStep {
  type: MacroStepType;
  button?: Button;
  stick?: Stick;
  x?: number;
  y?: number;
  durationMs?: number;
  count?: number;
  steps?: MacroStep[];
}

export interface Macro {
  id: string;
  name: string;
  description: string | null;
  steps: MacroStep[];
  /** Whole-macro passes; 0 = until stopped. */
  repeat: number;
  createdAt: string;
  updatedAt: string;
}

export interface MacroInput {
  name: string;
  description?: string | null;
  steps: MacroStep[];
  repeat: number;
}

export type MacroRunStatus = "running" | "paused" | "completed" | "stopped" | "failed";

export interface MacroRun {
  runId: string;
  macroId: string | null;
  macroName: string;
  status: MacroRunStatus;
  startedAt: string;
  endedAt: string | null;
  iteration: number;
  iterations: number | null;
  stepsExecuted: number;
  currentStep: string | null;
  error: string | null;
  isActive: boolean;
}

export interface ScreenFrame {
  url: string;
  sequence: number;
  capturedAt: string;
  captureMs: number;
}

export interface SwitchInput {
  name: string;
  host: string;
  port: number;
  enabled: boolean;
}

/** An RFC 9457 problem returned by the server. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
    readonly fieldErrors: Record<string, string[]> = {},
  ) {
    super(message);
  }
}

const TOKEN_KEY = "nxhub.token";

export function getToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

export function setToken(token: string | null): void {
  try {
    if (token) localStorage.setItem(TOKEN_KEY, token);
    else localStorage.removeItem(TOKEN_KEY);
  } catch {
    // Storage unavailable (private mode); the token lives only for this page.
  }
  memoryToken = token;
}

let memoryToken: string | null = getToken();

async function send(method: string, path: string, body: unknown, accept: string): Promise<Response> {
  const headers: Record<string, string> = { Accept: accept };
  if (memoryToken) headers.Authorization = `Bearer ${memoryToken}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";

  let response: Response;
  try {
    response = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  } catch {
    throw new ApiError(0, "network_error", "Cannot reach NX Hub. Check the server is running and you are on the same network.");
  }

  if (!response.ok) {
    const problem = (safeParse(await response.text()) ?? {}) as {
      code?: string;
      detail?: string;
      title?: string;
      errors?: Record<string, string[]>;
    };
    throw new ApiError(
      response.status,
      problem.code ?? "http_error",
      problem.detail ?? problem.title ?? response.statusText,
      problem.errors,
    );
  }
  return response;
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await send(method, path, body, "application/json");
  if (response.status === 204) return undefined as T;
  const text = await response.text();
  return (text ? safeParse(text) : undefined) as T;
}

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}

// Paths are relative to the page so the UI works at / and under a proxy sub-path such as /nxhub/.
const sw = (id: string) => `api/switches/${encodeURIComponent(id)}`;

export const api = {
  info: () => request<ServerInfo>("GET", "api/info"),
  list: () => request<SwitchInfo[]>("GET", "api/switches"),
  add: (input: SwitchInput) => request<SwitchInfo>("POST", "api/switches", input),
  update: (id: string, input: SwitchInput) => request<SwitchInfo>("PUT", sw(id), input),
  remove: (id: string) => request<void>("DELETE", sw(id)),
  connect: (id: string) => request<SwitchStatus>("POST", `${sw(id)}/connect`),
  disconnect: (id: string) => request<SwitchStatus>("POST", `${sw(id)}/disconnect`),
  activity: (id: string, limit = 50) => request<ActivityEntry[]>("GET", `${sw(id)}/activity?limit=${limit}`),
  diagnostics: (id: string, includeControllerTest: boolean) =>
    request<DiagnosticReport>("POST", `${sw(id)}/diagnostics`, { includeControllerTest }),
  latestDiagnostics: (id: string) => request<DiagnosticReport>("GET", `${sw(id)}/diagnostics/latest`),
  press: (id: string, button: Button, durationMs?: number) =>
    request<ControllerSnapshot>("POST", `${sw(id)}/controller/press`, { button, durationMs }),
  hold: (id: string, button: Button, maxHoldMs: number) =>
    request<ControllerSnapshot>("POST", `${sw(id)}/controller/hold`, { button, maxHoldMs }),
  release: (id: string, button: Button) => request<ControllerSnapshot>("POST", `${sw(id)}/controller/release`, { button }),
  stick: (id: string, stick: Stick, x: number, y: number, maxHoldMs: number) =>
    request<ControllerSnapshot>("POST", `${sw(id)}/controller/stick`, { stick, x, y, maxHoldMs }),
  releaseAll: (id: string) => request<ControllerSnapshot>("POST", `${sw(id)}/controller/release-all`, { everything: true }),
  resetController: (id: string) => request<ControllerSnapshot>("POST", `${sw(id)}/controller/reset`),

  /** Fetches a screen frame no older than maxAgeMs. The caller must revoke the returned object URL. */
  screen: async (id: string, maxAgeMs = 300): Promise<ScreenFrame> => {
    const response = await send("GET", `${sw(id)}/screen?maxAgeMs=${maxAgeMs}`, undefined, "image/jpeg");
    const blob = await response.blob();
    return {
      url: URL.createObjectURL(blob),
      sequence: Number(response.headers.get("X-Frame-Sequence") ?? 0),
      capturedAt: response.headers.get("X-Captured-At") ?? new Date().toISOString(),
      captureMs: Number(response.headers.get("X-Capture-Ms") ?? 0),
    };
  },

  macros: () => request<Macro[]>("GET", "api/macros"),
  addMacro: (input: MacroInput) => request<Macro>("POST", "api/macros", input),
  updateMacro: (macroId: string, input: MacroInput) => request<Macro>("PUT", `api/macros/${encodeURIComponent(macroId)}`, input),
  deleteMacro: (macroId: string) => request<void>("DELETE", `api/macros/${encodeURIComponent(macroId)}`),
  macroRun: (id: string) => request<{ run: MacroRun | null }>("GET", `${sw(id)}/macro`),
  runMacro: (id: string, macroId: string) => request<{ run: MacroRun }>("POST", `${sw(id)}/macro/run`, { macroId }),
  runInlineMacro: (id: string, macro: MacroInput) => request<{ run: MacroRun }>("POST", `${sw(id)}/macro/run`, { macro }),
  stopMacro: (id: string) => request<{ run: MacroRun | null }>("POST", `${sw(id)}/macro/stop`),
  pauseMacro: (id: string) => request<{ run: MacroRun }>("POST", `${sw(id)}/macro/pause`),
  resumeMacro: (id: string) => request<{ run: MacroRun }>("POST", `${sw(id)}/macro/resume`),
};

export function currentToken(): string | null {
  return memoryToken;
}
