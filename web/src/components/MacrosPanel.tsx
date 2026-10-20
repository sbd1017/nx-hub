import { useEffect, useMemo, useState } from "react";
import {
  ApiError,
  api,
  type Button,
  type Macro,
  type MacroInput,
  type MacroRun,
  type MacroStep,
  type MacroStepType,
  type Stick,
} from "../api";
import { BUTTON_NAMES, type ControllerInput } from "./VirtualController";

/** A controller input with the time it happened, captured while recording. */
export interface RecordedInput {
  at: number;
  input: ControllerInput;
}

interface Props {
  switchId: string;
  connected: boolean;
  /** Public demo: nothing is saved; edited or recorded macros can only be run once. */
  demo: boolean;
  macros: Macro[];
  run: MacroRun | null;
  recording: boolean;
  recorded: RecordedInput[];
  onStartRecording: () => void;
  onStopRecording: () => void;
  onChanged: () => void;
  onError: (message: string) => void;
}

const BUTTONS = Object.keys(BUTTON_NAMES) as Button[];
const STEP_TYPES: MacroStepType[] = ["press", "hold", "release", "stick", "wait"];
const STEP_LABEL: Record<MacroStepType, string> = {
  press: "Press",
  hold: "Hold",
  release: "Release",
  stick: "Stick",
  wait: "Wait",
  repeat: "Repeat",
};

export function MacrosPanel({
  switchId,
  connected,
  demo,
  macros,
  run,
  recording,
  recorded,
  onStartRecording,
  onStopRecording,
  onChanged,
  onError,
}: Props) {
  const [editing, setEditing] = useState<{ macro: Macro | null; draft: MacroInput } | null>(null);
  const active = run?.isActive ? run : null;

  const call = async (action: () => Promise<unknown>) => {
    try {
      await action();
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e));
    }
  };

  const stopRecording = () => {
    onStopRecording();
    const steps = stepsFromRecording(recorded);
    if (steps.length === 0) {
      onError("Nothing was recorded. Use the controller while recording.");
      return;
    }

    setEditing({
      macro: null,
      draft: { name: `Recording ${new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`, steps, repeat: 1 },
    });
  };

  if (editing) {
    return (
      <MacroEditor
        initial={editing.draft}
        existing={editing.macro}
        demo={demo}
        onRunNow={async (input) => {
          await api.runInlineMacro(switchId, input);
          setEditing(null);
        }}
        onCancel={() => setEditing(null)}
        onSaved={() => {
          setEditing(null);
          onChanged();
        }}
        onDelete={
          editing.macro && !demo
            ? async () => {
                await api.deleteMacro(editing.macro!.id);
                setEditing(null);
                onChanged();
              }
            : undefined
        }
      />
    );
  }

  return (
    <section className="card macros">
      <header className="card__head">
        <h3>Macros</h3>
        <div className="actions">
          {recording ? (
            <button type="button" className="btn btn--small btn--recording" onClick={stopRecording}>
              <span className="rec-dot" aria-hidden="true" /> Stop &amp; edit
            </button>
          ) : (
            <button
              type="button"
              className="btn btn--small"
              disabled={!connected || !!active}
              onClick={onStartRecording}
              title="Record what you do on the controller"
            >
              <span className="rec-dot rec-dot--idle" aria-hidden="true" /> Record
            </button>
          )}
          <button
            type="button"
            className="btn btn--small"
            onClick={() =>
              setEditing({ macro: null, draft: { name: "", steps: [{ type: "press", button: "a", durationMs: 100 }], repeat: 1 } })
            }
          >
            New
          </button>
        </div>
      </header>

      {recording && (
        <p className="notice notice--rec">
          Recording {recorded.length > 0 ? `· ${recorded.length} input${recorded.length === 1 ? "" : "s"}` : "· use the controller below"}
        </p>
      )}

      {active && (
        <div className="run" role="status">
          <div className="run__head">
            <span className={`run__status run__status--${active.status}`}>{active.status === "paused" ? "Paused" : "Running"}</span>
            <strong className="run__name">{active.macroName}</strong>
          </div>
          <div className="run__detail muted">
            Pass {active.iteration}
            {active.iterations ? ` of ${active.iterations}` : " · until stopped"} · {active.stepsExecuted} step
            {active.stepsExecuted === 1 ? "" : "s"}
            {active.currentStep ? ` · ${active.currentStep}` : ""}
          </div>
          <div className="actions">
            {active.status === "paused" ? (
              <button type="button" className="btn btn--small" onClick={() => void call(() => api.resumeMacro(switchId))}>
                Resume
              </button>
            ) : (
              <button type="button" className="btn btn--small" onClick={() => void call(() => api.pauseMacro(switchId))}>
                Pause
              </button>
            )}
            <button type="button" className="btn btn--small btn--danger" onClick={() => void call(() => api.stopMacro(switchId))}>
              Stop
            </button>
          </div>
        </div>
      )}

      {!active && run && run.status === "failed" && (
        <p className="notice notice--error">
          Last run of {run.macroName} failed: {run.error}
        </p>
      )}

      {macros.length === 0 ? (
        <p className="muted">No macros yet. Record one from the controller, or create one step by step.</p>
      ) : (
        <ul className="macro-list">
          {macros.map((m) => (
            <li key={m.id} className="macro-row">
              <div className="macro-row__text">
                <span className="macro-row__name">{m.name}</span>
                <span className="macro-row__meta muted">{summarise(m)}</span>
              </div>
              <div className="actions">
                <button
                  type="button"
                  className="btn btn--small"
                  onClick={() =>
                    setEditing({ macro: m, draft: { name: m.name, description: m.description, steps: m.steps, repeat: m.repeat } })
                  }
                >
                  Edit
                </button>
                <button
                  type="button"
                  className="btn btn--small btn--primary"
                  disabled={!connected || !!active || recording}
                  onClick={() => void call(() => api.runMacro(switchId, m.id))}
                >
                  Run
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

interface EditorProps {
  initial: MacroInput;
  existing: Macro | null;
  demo: boolean;
  onRunNow: (input: MacroInput) => Promise<void>;
  onCancel: () => void;
  onSaved: () => void;
  onDelete?: () => Promise<void>;
}

function MacroEditor({ initial, existing, demo, onRunNow, onCancel, onSaved, onDelete }: EditorProps) {
  const [name, setName] = useState(initial.name);
  const [repeat, setRepeat] = useState(String(initial.repeat));
  const [steps, setSteps] = useState<MacroStep[]>(initial.steps);
  const [jsonMode, setJsonMode] = useState(() => initial.steps.some((s) => s.type === "repeat"));
  const [json, setJson] = useState(() => JSON.stringify(initial.steps, null, 2));
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [message, setMessage] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (jsonMode) setJson(JSON.stringify(steps, null, 2));
  }, [jsonMode]); // eslint-disable-line react-hooks/exhaustive-deps

  const update = (index: number, change: Partial<MacroStep>) =>
    setSteps((list) => list.map((s, i) => (i === index ? normalise({ ...s, ...change }) : s)));
  const move = (index: number, by: number) =>
    setSteps((list) => {
      const next = [...list];
      const target = index + by;
      if (target < 0 || target >= next.length) return list;
      [next[index], next[target]] = [next[target]!, next[index]!];
      return next;
    });

  const save = async () => {
    setSaving(true);
    setErrors({});
    setMessage(null);
    try {
      let body = steps;
      if (jsonMode) {
        try {
          body = JSON.parse(json) as MacroStep[];
        } catch {
          setMessage("The steps are not valid JSON.");
          return;
        }
      }

      const input: MacroInput = {
        name: name || "Recording",
        description: initial.description ?? null,
        steps: body,
        repeat: Number(repeat),
      };
      if (demo) {
        await onRunNow(input);
        return;
      }

      if (existing) await api.updateMacro(existing.id, input);
      else await api.addMacro(input);
      onSaved();
    } catch (e) {
      if (e instanceof ApiError) {
        setErrors(e.fieldErrors);
        setMessage(
          Object.keys(e.fieldErrors).length > 0
            ? Object.entries(e.fieldErrors)
                .map(([k, v]) => `${k}: ${v[0]}`)
                .join(" · ")
            : e.message,
        );
      } else {
        setMessage(String(e));
      }
    } finally {
      setSaving(false);
    }
  };

  return (
    <section className="card macro-editor">
      <header className="card__head">
        <h3>{existing ? "Edit macro" : "New macro"}</h3>
        <button type="button" className="btn btn--small" onClick={() => setJsonMode((v) => !v)} aria-pressed={jsonMode}>
          {jsonMode ? "Step list" : "JSON"}
        </button>
      </header>

      <div className="macro-editor__fields">
        <label className="field">
          <span>Name</span>
          <input value={name} onChange={(e) => setName(e.target.value)} maxLength={64} placeholder="Skip intro" />
          {errors.name && <small className="field__error">{errors.name[0]}</small>}
        </label>
        <label className="field field--narrow">
          <span>Repeat</span>
          <input value={repeat} onChange={(e) => setRepeat(e.target.value)} inputMode="numeric" title="0 = until stopped" />
          {errors.repeat ? <small className="field__error">{errors.repeat[0]}</small> : <small className="muted">0 = until stopped</small>}
        </label>
      </div>

      {jsonMode ? (
        <label className="field">
          <span>Steps (JSON). Repeat steps nest: {`{"type":"repeat","count":3,"steps":[…]}`}</span>
          <textarea className="macro-json" value={json} onChange={(e) => setJson(e.target.value)} spellCheck={false} rows={12} />
        </label>
      ) : (
        <ol className="steps">
          {steps.map((step, i) => (
            <li key={i} className="step">
              <span className="step__index muted">{i + 1}</span>
              <select
                className="step__type"
                value={step.type}
                onChange={(e) => update(i, { type: e.target.value as MacroStepType })}
                aria-label="Step type"
              >
                {STEP_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {STEP_LABEL[t]}
                  </option>
                ))}
              </select>
              <div className="step__fields">
                {(step.type === "press" || step.type === "hold" || step.type === "release") && (
                  <select value={step.button ?? "a"} onChange={(e) => update(i, { button: e.target.value as Button })} aria-label="Button">
                    {BUTTONS.map((b) => (
                      <option key={b} value={b}>
                        {BUTTON_NAMES[b]}
                      </option>
                    ))}
                  </select>
                )}
                {step.type === "stick" && (
                  <>
                    <select value={step.stick ?? "left"} onChange={(e) => update(i, { stick: e.target.value as Stick })} aria-label="Stick">
                      <option value="left">Left stick</option>
                      <option value="right">Right stick</option>
                    </select>
                    <NumberInput label="x" value={step.x ?? 0} step={0.1} onChange={(x) => update(i, { x })} />
                    <NumberInput label="y" value={step.y ?? 0} step={0.1} onChange={(y) => update(i, { y })} />
                  </>
                )}
                {step.type === "repeat" && (
                  <span className="muted">
                    ×{step.count} ({step.steps?.length ?? 0} steps): edit as JSON
                  </span>
                )}
                {(step.type === "press" || step.type === "wait" || step.type === "stick") && (
                  <NumberInput
                    label="ms"
                    value={step.durationMs ?? (step.type === "press" ? 100 : step.type === "wait" ? 500 : undefined)}
                    step={10}
                    optional={step.type === "stick"}
                    onChange={(durationMs) => update(i, { durationMs })}
                  />
                )}
              </div>
              <div className="step__tools">
                <IconButton label="Move up" disabled={i === 0} onClick={() => move(i, -1)} path="M6 12.5 10 8l4 4.5" />
                <IconButton label="Move down" disabled={i === steps.length - 1} onClick={() => move(i, 1)} path="M6 7.5 10 12l4-4.5" />
                <IconButton
                  label="Remove step"
                  onClick={() => setSteps((list) => list.filter((_, j) => j !== i))}
                  path="M6 6l8 8M14 6l-8 8"
                />
              </div>
            </li>
          ))}
        </ol>
      )}

      {!jsonMode && (
        <div className="actions">
          <button
            type="button"
            className="btn btn--small"
            onClick={() => setSteps((list) => [...list, { type: "press", button: "a", durationMs: 100 }])}
          >
            Add step
          </button>
          <button type="button" className="btn btn--small" onClick={() => setSteps((list) => [...list, { type: "wait", durationMs: 500 }])}>
            Add wait
          </button>
        </div>
      )}

      {demo && <p className="muted macro-editor__note">The demo doesn't save macros: run it now to try it on the simulated Switch.</p>}
      {message && <p className="notice notice--error">{message}</p>}

      <div className="actions actions--split">
        {onDelete ? (
          <button
            type="button"
            className="btn btn--danger"
            onClick={() => {
              if (confirm(`Delete ${existing?.name}?`)) void onDelete();
            }}
          >
            Delete
          </button>
        ) : (
          <span />
        )}
        <div className="actions">
          <button type="button" className="btn" onClick={onCancel}>
            Cancel
          </button>
          <button type="button" className="btn btn--primary" disabled={saving} onClick={() => void save()}>
            {saving ? (demo ? "Starting…" : "Saving…") : demo ? "Run now" : "Save"}
          </button>
        </div>
      </div>
    </section>
  );
}

function NumberInput({
  label,
  value,
  step,
  optional,
  onChange,
}: {
  label: string;
  value: number | undefined;
  step: number;
  optional?: boolean;
  onChange: (v: number | undefined) => void;
}) {
  return (
    <label className="num">
      <input
        type="number"
        inputMode="decimal"
        step={step}
        value={value ?? ""}
        placeholder={optional ? "hold" : undefined}
        onChange={(e) => onChange(e.target.value === "" ? undefined : Number(e.target.value))}
      />
      <span>{label}</span>
    </label>
  );
}

function IconButton({ label, path, disabled, onClick }: { label: string; path: string; disabled?: boolean; onClick: () => void }) {
  return (
    <button type="button" className="icon-button icon-button--small" aria-label={label} title={label} disabled={disabled} onClick={onClick}>
      <svg
        width="16"
        height="16"
        viewBox="0 0 20 20"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.75"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <path d={path} />
      </svg>
    </button>
  );
}

/** Keeps only the fields that apply to the step's type. */
function normalise(step: MacroStep): MacroStep {
  switch (step.type) {
    case "press":
      return { type: "press", button: step.button ?? "a", durationMs: step.durationMs ?? 100 };
    case "hold":
    case "release":
      return { type: step.type, button: step.button ?? "a" };
    case "stick":
      return { type: "stick", stick: step.stick ?? "left", x: step.x ?? 0, y: step.y ?? 0, durationMs: step.durationMs };
    case "wait":
      return { type: "wait", durationMs: step.durationMs ?? 500 };
    default:
      return step;
  }
}

function summarise(m: Macro): string {
  const count = countSteps(m.steps);
  const seconds = estimateMs(m.steps) / 1000;
  const time = seconds < 60 ? `~${seconds.toFixed(seconds < 10 ? 1 : 0)} s` : `~${(seconds / 60).toFixed(1)} min`;
  const repeat = m.repeat === 0 ? " · loops until stopped" : m.repeat > 1 ? ` · ×${m.repeat}` : "";
  return `${count} step${count === 1 ? "" : "s"} · ${time}${repeat}`;
}

function countSteps(steps: MacroStep[]): number {
  return steps.reduce((n, s) => n + (s.type === "repeat" ? countSteps(s.steps ?? []) : 1), 0);
}

function estimateMs(steps: MacroStep[]): number {
  // Includes a rough 60 ms per command for the round trip to the Switch.
  return steps.reduce((ms, s) => {
    switch (s.type) {
      case "press":
        return ms + (s.durationMs ?? 100) + 60;
      case "wait":
        return ms + (s.durationMs ?? 0);
      case "stick":
        return ms + (s.durationMs ?? 0) + 60;
      case "repeat":
        return ms + (s.count ?? 1) * estimateMs(s.steps ?? []);
      default:
        return ms + 60;
    }
  }, 0);
}

/**
 * Turns a recording into steps: waits between inputs, hold/release pairs with nothing in between
 * become a single timed press, and stick moves become stick steps (recentring is recorded too).
 */
export function stepsFromRecording(events: RecordedInput[]): MacroStep[] {
  const raw: MacroStep[] = [];
  let last: number | null = null;
  for (const { at, input } of events) {
    if (last !== null) {
      const gap = Math.round(at - last);
      if (gap >= 20) raw.push({ type: "wait", durationMs: Math.min(gap, 3_600_000) });
    }

    last = at;
    if (input.kind === "stick") raw.push({ type: "stick", stick: input.stick, x: input.x, y: input.y });
    else raw.push({ type: input.kind === "down" ? "hold" : "release", button: input.button });
  }

  // hold X, [wait d], release X  →  press X for d
  const steps: MacroStep[] = [];
  for (let i = 0; i < raw.length; i++) {
    const step = raw[i]!;
    const next = raw[i + 1];
    const after = raw[i + 2];
    if (step.type === "hold" && next?.type === "release" && next.button === step.button) {
      steps.push({ type: "press", button: step.button, durationMs: 20 });
      i += 1;
    } else if (step.type === "hold" && next?.type === "wait" && after?.type === "release" && after.button === step.button) {
      steps.push({ type: "press", button: step.button, durationMs: Math.min(Math.max(next.durationMs ?? 100, 20), 10_000) });
      i += 2;
    } else {
      steps.push(step);
    }
  }

  return steps;
}

export function useRecorder() {
  const [recording, setRecording] = useState(false);
  const [recorded, setRecorded] = useState<RecordedInput[]>([]);
  return useMemo(
    () => ({
      recording,
      recorded,
      start: () => {
        setRecorded([]);
        setRecording(true);
      },
      stop: () => setRecording(false),
      capture: (input: ControllerInput) => {
        setRecorded((list) => [...list, { at: performance.now(), input }]);
      },
    }),
    [recording, recorded],
  );
}
