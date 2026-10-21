import { useCallback, useEffect, useMemo, useState } from "react";
import {
  ApiError,
  api,
  setToken,
  type ActivityEntry,
  type AppLink,
  type ControllerSnapshot,
  type DiagnosticReport,
  type Macro,
  type MacroRun,
  type SwitchInfo,
  type SwitchStatus,
} from "./api";
import { useHubEvents, type HubEvent } from "./events";
import { ActivityList, DiagnosticsPanel, StateDot, StatusCard, SwitchForm, TokenGate } from "./components/Panels";
import { VirtualController, type ControllerPalette } from "./components/VirtualController";
import { MacrosPanel, useRecorder } from "./components/MacrosPanel";
import { ScreenPanel } from "./components/ScreenPanel";

type Phase = "loading" | "needs-token" | "ready" | "unreachable";

const SELECTED_KEY = "nxhub.selected";
const PALETTE_KEY = "nxhub.controllerPalette";

export function App() {
  const [phase, setPhase] = useState<Phase>("loading");
  const [tokenError, setTokenError] = useState<string | null>(null);
  const [switches, setSwitches] = useState<SwitchInfo[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(() => readSelected());
  const [editing, setEditing] = useState(false);
  const [adding, setAdding] = useState(false);
  const [activity, setActivity] = useState<ActivityEntry[]>([]);
  const [report, setReport] = useState<DiagnosticReport | null>(null);
  const [running, setRunning] = useState(false);
  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState<string | null>(null);
  const [version, setVersion] = useState("");
  const [demo, setDemo] = useState(false);
  const [links, setLinks] = useState<AppLink[]>([]);
  const [macros, setMacros] = useState<Macro[]>([]);
  const [macroRun, setMacroRun] = useState<MacroRun | null>(null);
  const recorder = useRecorder();
  const [palette, setPalette] = useState<ControllerPalette>(() => (readStored(PALETTE_KEY) === "graphite" ? "graphite" : "neon"));

  const selected = useMemo(() => switches.find((s) => s.id === selectedId) ?? switches[0] ?? null, [switches, selectedId]);

  const showError = useCallback((message: string) => {
    setToast(message);
    window.setTimeout(() => setToast((current) => (current === message ? null : current)), 5000);
  }, []);

  const refreshMacros = useCallback(() => {
    api
      .macros()
      .then(setMacros)
      .catch(() => undefined);
  }, []);

  const refresh = useCallback(async () => {
    try {
      const list = await api.list();
      setSwitches(list);
      setPhase("ready");
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        setPhase("needs-token");
      } else {
        setPhase("unreachable");
      }
    }
  }, []);

  useEffect(() => {
    api
      .info()
      .then((info) => {
        setVersion(info.version);
        setDemo(info.demo);
        setLinks(info.links ?? []);
        // Dark unless this install opted into another theme.
        if (info.theme !== "dark") document.documentElement.dataset.theme = info.theme;
      })
      .catch(() => undefined);
    void refresh();
  }, [refresh]);

  // Load activity and the latest report whenever the selection changes.
  useEffect(() => {
    if (!selected || phase !== "ready") return;
    let cancelled = false;
    api
      .activity(selected.id)
      .then((a) => !cancelled && setActivity(a))
      .catch(() => undefined);
    api
      .macroRun(selected.id)
      .then((r) => !cancelled && setMacroRun(r.run))
      .catch(() => undefined);
    refreshMacros();
    api
      .latestDiagnostics(selected.id)
      .then((r) => !cancelled && setReport(r))
      .catch(() => !cancelled && setReport(null));
    return () => {
      cancelled = true;
    };
  }, [selected?.id, phase]); // eslint-disable-line react-hooks/exhaustive-deps

  const onEvent = useCallback(
    (event: HubEvent) => {
      switch (event.type) {
        case "switch.state": {
          const status = event.data as SwitchStatus;
          setSwitches((list) => list.map((s) => (s.id === status.switchId ? { ...s, status } : s)));
          break;
        }
        case "controller.state": {
          const controller = event.data as ControllerSnapshot;
          setSwitches((list) => list.map((s) => (s.id === event.switchId ? { ...s, status: { ...s.status, controller } } : s)));
          break;
        }
        case "macro.state":
          if (event.switchId === selected?.id) setMacroRun(event.data as MacroRun);
          break;
        case "macro.saved":
        case "macro.deleted":
          refreshMacros();
          break;
        case "activity":
          if (event.switchId === selected?.id) {
            setActivity((list) => [event.data as ActivityEntry, ...list].slice(0, 50));
          }
          break;
        case "diagnostics.completed":
          if (event.switchId === selected?.id) setReport(event.data as DiagnosticReport);
          break;
        case "switch.added":
        case "switch.updated":
        case "switch.removed":
          void refresh();
          break;
      }
    },
    [refresh, refreshMacros, selected?.id],
  );

  const link = useHubEvents(onEvent, phase === "ready");

  const select = (id: string) => {
    setSelectedId(id);
    writeStored(SELECTED_KEY, id);
  };

  const run = async (action: () => Promise<unknown>) => {
    setBusy(true);
    try {
      await action();
    } catch (error) {
      showError(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  };

  if (phase === "loading") {
    return <main className="gate muted">Loading…</main>;
  }

  if (phase === "needs-token") {
    return (
      <TokenGate
        error={tokenError}
        onSubmit={async (token) => {
          setToken(token);
          try {
            await api.list();
            setTokenError(null);
            await refresh();
          } catch {
            setToken(null);
            setTokenError("That token was not accepted.");
          }
        }}
      />
    );
  }

  if (phase === "unreachable") {
    return (
      <main className="gate">
        <section className="card">
          <h2>Cannot reach NX Hub</h2>
          <p className="muted">The server did not respond. Check it is running, then retry.</p>
          <button type="button" className="btn btn--primary" onClick={() => void refresh()}>
            Retry
          </button>
        </section>
      </main>
    );
  }

  const connected = selected?.status.state === "connected";
  const heldCount = selected ? selected.status.controller.heldButtons.length + Object.keys(selected.status.controller.sticks).length : 0;

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <img src="icon.svg" alt="" width={24} height={24} />
          <span>NX Hub</span>
        </div>
        <nav className="switcher" aria-label="Switches">
          {switches.map((s) => (
            <button key={s.id} type="button" className={`chip ${s.id === selected?.id ? "chip--active" : ""}`} onClick={() => select(s.id)}>
              <StateDot state={s.status.state} />
              {s.name}
            </button>
          ))}
          {switches.length > 0 && !demo && (
            <button type="button" className="chip chip--add" onClick={() => setAdding(true)} aria-label="Add a Switch">
              +
            </button>
          )}
        </nav>
        {links.length > 0 && (
          <nav className="apps" aria-label="Other pages">
            {links.map((l) => (
              <a key={l.url} className="chip chip--app" href={l.url}>
                {l.name}
              </a>
            ))}
          </nav>
        )}
        <span className={`link link--${link}`} title={`Live updates: ${link}`}>
          {link === "live" ? "Live" : link === "connecting" ? "…" : "Offline"}
        </span>
      </header>

      {demo && (
        <div className="demo-banner" role="note">
          <strong>Live demo.</strong> You're controlling a simulated Switch shared with other visitors: try the D-pad, A and B with live
          view on. Nothing here touches real hardware. <a href="https://github.com/sbd1017/nx-hub">Get NX Hub</a>
        </div>
      )}

      {toast && (
        <div className="toast" role="alert" onClick={() => setToast(null)}>
          {toast}
        </div>
      )}

      <main className="content">
        {(switches.length === 0 || adding) && (
          <SwitchForm
            onCancel={switches.length > 0 ? () => setAdding(false) : undefined}
            onSave={async (input) => {
              const created = await api.add(input);
              setAdding(false);
              select(created.id);
              await refresh();
            }}
          />
        )}

        {selected && !adding && (
          <>
            {/* Two stacked columns on wide screens; on phones the slots interleave by `order`. */}
            <div className="column column--main">
              <div className="slot slot--status">
                {editing ? (
                  <SwitchForm
                    initial={selected}
                    onCancel={() => setEditing(false)}
                    onSave={async (input) => {
                      await api.update(selected.id, input);
                      setEditing(false);
                      await refresh();
                    }}
                    onDelete={async () => {
                      await api.remove(selected.id);
                      setEditing(false);
                      await refresh();
                    }}
                  />
                ) : (
                  <StatusCard
                    demo={demo}
                    device={selected}
                    links={links}
                    busy={busy}
                    onEdit={() => setEditing(true)}
                    onConnect={() => void run(() => api.connect(selected.id))}
                    onDisconnect={() => void run(() => api.disconnect(selected.id))}
                  />
                )}
              </div>

              <section className="slot slot--controller card card--controller">
                <header className="card__head">
                  <h3>
                    Controller
                    {heldCount > 0 && <span className="muted"> · {heldCount} held</span>}
                  </h3>
                  <div className="actions">
                    <button
                      type="button"
                      className={`swatch swatch--${palette === "neon" ? "graphite" : "neon"}`}
                      onClick={() => {
                        const next = palette === "neon" ? "graphite" : "neon";
                        setPalette(next);
                        writeStored(PALETTE_KEY, next);
                      }}
                      aria-label={palette === "neon" ? "Use graphite controller colours" : "Use neon Joy-Con colours"}
                      title={palette === "neon" ? "Graphite colours" : "Neon Joy-Con colours"}
                    />
                    <button
                      type="button"
                      className="btn btn--small"
                      disabled={!connected || busy}
                      onClick={() => void run(() => api.releaseAll(selected.id))}
                    >
                      Release all
                    </button>
                    <button
                      type="button"
                      className="btn btn--small"
                      disabled={!connected || busy}
                      onClick={() => void run(() => api.resetController(selected.id))}
                      title="Release everything and detach the virtual controller"
                    >
                      Reset
                    </button>
                  </div>
                </header>
                {!connected && <p className="muted">Connect the Switch to use the controller.</p>}
                <VirtualController
                  switchId={selected.id}
                  enabled={connected}
                  palette={palette}
                  onError={showError}
                  onInput={recorder.recording ? recorder.capture : undefined}
                />
              </section>
            </div>

            <div className="column column--side">
              <div className="slot slot--screen">
                <ScreenPanel switchId={selected.id} connected={connected} />
              </div>

              <div className="slot slot--macros">
                <MacrosPanel
                  demo={demo}
                  switchId={selected.id}
                  connected={connected}
                  macros={macros}
                  run={macroRun}
                  recording={recorder.recording}
                  recorded={recorder.recorded}
                  onStartRecording={recorder.start}
                  onStopRecording={recorder.stop}
                  onChanged={refreshMacros}
                  onError={showError}
                />
              </div>

              <div className="slot slot--checks">
                <DiagnosticsPanel
                  report={report}
                  running={running}
                  onRun={async (includeControllerTest) => {
                    setRunning(true);
                    try {
                      setReport(await api.diagnostics(selected.id, includeControllerTest));
                    } catch (error) {
                      showError(error instanceof Error ? error.message : String(error));
                    } finally {
                      setRunning(false);
                    }
                  }}
                />
              </div>

              <div className="slot slot--activity">
                <ActivityList entries={activity} />
              </div>
            </div>
          </>
        )}
      </main>

      <footer className="footer muted">
        NX Hub {version} · <a href="docs/">API</a> · Not affiliated with Nintendo
      </footer>
    </div>
  );
}

function readSelected(): string | null {
  return readStored(SELECTED_KEY);
}

function readStored(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeStored(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Per-device preference only; fine if it is not kept.
  }
}
