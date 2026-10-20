import { useEffect, useState, type FormEvent } from "react";
import { ApiError, type ActivityEntry, type CheckStatus, type DiagnosticReport, type SwitchInfo, type SwitchInput } from "../api";

const STATE_LABEL: Record<string, string> = {
  connected: "Connected",
  connecting: "Connecting…",
  reconnecting: "Reconnecting",
  disconnecting: "Disconnecting…",
  disconnected: "Disconnected",
};

export function StateDot({ state }: { state: string }) {
  return <span className={`dot dot--${state}`} aria-hidden="true" />;
}

function useNow(intervalMs = 1000) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(id);
  }, [intervalMs]);
  return now;
}

function relative(iso: string | null | undefined, now: number): string {
  if (!iso) return "—";
  const seconds = Math.round((now - Date.parse(iso)) / 1000);
  if (seconds < 0) return `in ${-seconds}s`;
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  return `${Math.floor(seconds / 3600)}h ago`;
}

const IMPLEMENTATION: Record<string, string> = {
  sysBotBase: "sys-botbase",
  sysBotBaseCpp: "sys-botbase-cpp",
  unknown: "unknown module",
};

interface StatusCardProps {
  device: SwitchInfo;
  demo?: boolean;
  busy: boolean;
  onConnect: () => void;
  onDisconnect: () => void;
  onEdit: () => void;
}

export function StatusCard({ device, demo = false, busy, onConnect, onDisconnect, onEdit }: StatusCardProps) {
  const now = useNow();
  const s = device.status;
  const caps = s.capabilities;
  const active = s.state === "connected" || s.state === "connecting" || s.state === "reconnecting";

  return (
    <section className="card status">
      <header className="status__head">
        <div>
          <h2 className="status__name">{device.name}</h2>
          <p className="muted mono">{demo ? "Simulated Switch" : `${device.host}:${device.port}`}</p>
        </div>
        {!demo && (
          <button type="button" className="icon-button" onClick={onEdit} aria-label="Edit Switch settings">
            <svg
              width="20"
              height="20"
              viewBox="0 0 20 20"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.6"
              strokeLinecap="round"
              strokeLinejoin="round"
              aria-hidden="true"
            >
              <circle cx="10" cy="10" r="2.6" />
              <path d="M10 2.5v2M10 15.5v2M2.5 10h2M15.5 10h2M4.7 4.7l1.4 1.4M13.9 13.9l1.4 1.4M4.7 15.3l1.4-1.4M13.9 6.1l1.4-1.4" />
            </svg>
          </button>
        )}
      </header>

      <div className={`state state--${s.state}`}>
        <StateDot state={s.state} />
        <span className="state__label">{STATE_LABEL[s.state] ?? s.state}</span>
        {s.state === "reconnecting" && s.nextAttemptAt && <span className="muted">· retry {relative(s.nextAttemptAt, now)}</span>}
      </div>

      <dl className="facts">
        <div>
          <dt>Latency</dt>
          <dd className="mono">{s.latency ? `${s.latency.lastMs.toFixed(0)} ms` : "—"}</dd>
        </div>
        <div>
          <dt>Average</dt>
          <dd className="mono">{s.latency ? `${s.latency.averageMs.toFixed(0)} ms` : "—"}</dd>
        </div>
        <div>
          <dt>Drops</dt>
          <dd className="mono">{s.connectionLossCount}</dd>
        </div>
        <div className="facts__wide">
          <dt>Module</dt>
          <dd>{caps ? `${IMPLEMENTATION[caps.implementation]} ${caps.rawVersion}` : "—"}</dd>
        </div>
        <div>
          <dt>Connected</dt>
          <dd>{s.connectedSince ? relative(s.connectedSince, now) : "—"}</dd>
        </div>
      </dl>

      {s.lastError && s.state !== "connected" && <p className="notice notice--error">{s.lastError.message}</p>}
      {caps?.warnings.map((w) => (
        <p key={w} className="notice notice--warn">
          {w}
        </p>
      ))}
      {s.controller.pendingNeutralise && (
        <p className="notice notice--warn">Inputs may still be held on the Switch. They will be released when it reconnects.</p>
      )}

      {!demo && (
        <div className="actions">
          {active ? (
            <button type="button" className="btn" disabled={busy} onClick={onDisconnect}>
              Disconnect
            </button>
          ) : (
            <button type="button" className="btn btn--primary" disabled={busy} onClick={onConnect}>
              Connect
            </button>
          )}
        </div>
      )}
    </section>
  );
}

const CHECK_ICON: Record<CheckStatus, string> = { pass: "✓", warn: "!", fail: "✕", skipped: "–", info: "i" };
const OVERALL: Record<DiagnosticReport["overall"], string> = { ready: "READY", degraded: "DEGRADED", notReady: "NOT READY" };

interface DiagnosticsProps {
  report: DiagnosticReport | null;
  running: boolean;
  onRun: (includeControllerTest: boolean) => void;
}

export function DiagnosticsPanel({ report, running, onRun }: DiagnosticsProps) {
  const [controllerTest, setControllerTest] = useState(false);
  return (
    <section className="card">
      <header className="card__head">
        <h3>System check</h3>
        {report && <span className={`badge badge--${report.overall}`}>{OVERALL[report.overall]}</span>}
      </header>
      {report ? (
        <ul className="checks">
          {report.checks.map((c) => (
            <li key={c.id} className={`check check--${c.status}`}>
              <span className="check__icon" aria-label={c.status}>
                {CHECK_ICON[c.status]}
              </span>
              <div>
                <div className="check__title">{c.title}</div>
                <div className="check__message">{c.message}</div>
              </div>
            </li>
          ))}
        </ul>
      ) : (
        <p className="muted">Run a check to test the network path, sys-botbase and latency.</p>
      )}
      <div className="actions actions--split">
        <label className="toggle">
          <input type="checkbox" checked={controllerTest} onChange={(e) => setControllerTest(e.target.checked)} />
          <span>Include controller test</span>
        </label>
        <button type="button" className="btn" disabled={running} onClick={() => onRun(controllerTest)}>
          {running ? "Checking…" : "Run check"}
        </button>
      </div>
    </section>
  );
}

export function ActivityList({ entries }: { entries: ActivityEntry[] }) {
  const now = useNow(5000);
  return (
    <section className="card">
      <header className="card__head">
        <h3>Recent activity</h3>
      </header>
      {entries.length === 0 ? (
        <p className="muted">Nothing yet.</p>
      ) : (
        <ol className="activity">
          {entries.map((e) => (
            <li key={e.sequence} className={`activity__item activity__item--${e.level}`}>
              <span className="activity__time" title={new Date(e.timestamp).toLocaleString()}>
                {relative(e.timestamp, now)}
              </span>
              <span className="activity__message">{e.message}</span>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}

interface SwitchFormProps {
  initial?: SwitchInfo;
  onSave: (input: SwitchInput) => Promise<void>;
  onCancel?: () => void;
  onDelete?: () => Promise<void>;
}

export function SwitchForm({ initial, onSave, onCancel, onDelete }: SwitchFormProps) {
  const [name, setName] = useState(initial?.name ?? "");
  const [host, setHost] = useState(initial?.host ?? "");
  const [port, setPort] = useState(String(initial?.port ?? 6000));
  const [enabled, setEnabled] = useState(initial?.enabled ?? true);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [message, setMessage] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setErrors({});
    setMessage(null);
    try {
      await onSave({ name, host, port: Number(port), enabled });
    } catch (error) {
      if (error instanceof ApiError) {
        setErrors(error.fieldErrors);
        setMessage(error.message);
      } else {
        setMessage(String(error));
      }
    } finally {
      setSaving(false);
    }
  };

  const field = (key: string) => errors[key]?.[0];

  return (
    <form className="card form" onSubmit={submit} noValidate>
      <h3>{initial ? "Switch settings" : "Add your Switch"}</h3>
      {!initial && (
        <p className="muted">
          The Switch needs Atmosphère with sys-botbase installed, on the same network as NX Hub. Find its IP under System Settings →
          Internet.
        </p>
      )}
      <label className="field">
        <span>Name</span>
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="OLED" maxLength={64} required />
        {field("name") && <small className="field__error">{field("name")}</small>}
      </label>
      <label className="field">
        <span>IP address or hostname</span>
        <input
          value={host}
          onChange={(e) => setHost(e.target.value)}
          placeholder="192.168.1.50"
          inputMode="url"
          autoCapitalize="off"
          autoCorrect="off"
          spellCheck={false}
          required
        />
        {field("host") && <small className="field__error">{field("host")}</small>}
      </label>
      <label className="field">
        <span>sys-botbase port</span>
        <input value={port} onChange={(e) => setPort(e.target.value)} inputMode="numeric" pattern="[0-9]*" />
        {field("port") && <small className="field__error">{field("port")}</small>}
      </label>
      <label className="toggle">
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        <span>Keep connected automatically</span>
      </label>
      {message && Object.keys(errors).length === 0 && <p className="notice notice--error">{message}</p>}
      <div className="actions actions--split">
        {onDelete ? (
          <button
            type="button"
            className="btn btn--danger"
            onClick={() => {
              if (confirm(`Remove ${initial?.name}? Held inputs are released first.`)) void onDelete();
            }}
          >
            Remove
          </button>
        ) : (
          <span />
        )}
        <div className="actions">
          {onCancel && (
            <button type="button" className="btn" onClick={onCancel}>
              Cancel
            </button>
          )}
          <button type="submit" className="btn btn--primary" disabled={saving}>
            {saving ? "Saving…" : "Save"}
          </button>
        </div>
      </div>
    </form>
  );
}

export function TokenGate({ onSubmit, error }: { onSubmit: (token: string) => void; error: string | null }) {
  const [token, setToken] = useState("");
  return (
    <main className="gate">
      <form
        className="card form"
        onSubmit={(e) => {
          e.preventDefault();
          onSubmit(token.trim());
        }}
      >
        <h2>NX Hub</h2>
        <p className="muted">Enter the admin token. It was generated on first start; on the Raspberry Pi read it with:</p>
        <pre className="code">sudo cat /var/lib/nxhub/admin-token</pre>
        <label className="field">
          <span>Admin token</span>
          <input
            value={token}
            onChange={(e) => setToken(e.target.value)}
            autoComplete="current-password"
            type="password"
            autoCapitalize="off"
            required
          />
        </label>
        {error && <p className="notice notice--error">{error}</p>}
        <div className="actions">
          <button type="submit" className="btn btn--primary">
            Continue
          </button>
        </div>
      </form>
    </main>
  );
}
