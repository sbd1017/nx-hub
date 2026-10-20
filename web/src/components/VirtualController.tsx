import { useCallback, useEffect, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode } from "react";
import { api, type Button, type Stick } from "../api";

/** Server-side safety limit for holds from the UI; refreshed while a finger stays down. */
const HOLD_LIMIT_MS = 10_000;
const HOLD_REFRESH_MS = 4_000;
const STICK_INTERVAL_MS = 60;

export type ControllerPalette = "neon" | "graphite";

/** What the user did on the virtual controller, for the macro recorder. */
export type ControllerInput = { kind: "down" | "up"; button: Button } | { kind: "stick"; stick: Stick; x: number; y: number };

interface Props {
  switchId: string;
  enabled: boolean;
  palette: ControllerPalette;
  onError: (message: string) => void;
  /** Called for every input sent, so it can be recorded. */
  onInput?: (input: ControllerInput) => void;
}

/**
 * Touch-first virtual Pro Controller. Pointer down = hold, pointer up = release.
 * Requests for each input are chained so a release can never overtake its hold.
 */
export function VirtualController({ switchId, enabled, palette, onError, onInput }: Props) {
  const inputListener = useRef(onInput);
  inputListener.current = onInput;
  const chains = useRef(new Map<string, Promise<unknown>>());
  const held = useRef(new Set<Button>());
  const refreshers = useRef(new Map<Button, number>());
  const [pressed, setPressed] = useState<ReadonlySet<Button>>(new Set());

  const enqueue = useCallback(
    (key: string, action: () => Promise<unknown>) => {
      const previous = chains.current.get(key) ?? Promise.resolve();
      const next = previous.then(action).catch((error: unknown) => {
        onError(error instanceof Error ? error.message : String(error));
      });
      chains.current.set(key, next);
      return next;
    },
    [onError],
  );

  const down = useCallback(
    (button: Button) => {
      if (!enabled || held.current.has(button)) return;
      held.current.add(button);
      setPressed(new Set(held.current));
      navigator.vibrate?.(8);
      inputListener.current?.({ kind: "down", button });
      enqueue(button, () => api.hold(switchId, button, HOLD_LIMIT_MS));
      refreshers.current.set(
        button,
        window.setInterval(() => enqueue(button, () => api.hold(switchId, button, HOLD_LIMIT_MS)), HOLD_REFRESH_MS),
      );
    },
    [enabled, enqueue, switchId],
  );

  const up = useCallback(
    (button: Button) => {
      if (!held.current.delete(button)) return;
      setPressed(new Set(held.current));
      window.clearInterval(refreshers.current.get(button));
      refreshers.current.delete(button);
      inputListener.current?.({ kind: "up", button });
      enqueue(button, () => api.release(switchId, button));
    },
    [enqueue, switchId],
  );

  // Never leave inputs held when the page is hidden or closed.
  useEffect(() => {
    const releaseEverything = () => {
      for (const button of [...held.current]) up(button);
    };
    const onVisibility = () => document.visibilityState === "hidden" && releaseEverything();
    document.addEventListener("visibilitychange", onVisibility);
    window.addEventListener("pagehide", releaseEverything);
    window.addEventListener("blur", releaseEverything);
    return () => {
      document.removeEventListener("visibilitychange", onVisibility);
      window.removeEventListener("pagehide", releaseEverything);
      window.removeEventListener("blur", releaseEverything);
      releaseEverything();
    };
  }, [up]);

  const pad = (button: Button, label: ReactNode, className = "") => (
    <PadButton
      key={button}
      button={button}
      label={label}
      className={className}
      active={pressed.has(button)}
      disabled={!enabled}
      onDown={down}
      onUp={up}
    />
  );

  return (
    <div
      className={`controller controller--${palette} ${enabled ? "" : "controller--disabled"}`}
      role="group"
      aria-label="Virtual controller"
    >
      {/* Laid out like a pair of Joy-Con: left has minus, stick, D-pad, capture; right has plus, face buttons, stick, HOME. */}
      <section className="joycon joycon--left" aria-label="Left Joy-Con">
        <div className="joycon__shoulders">
          {pad("zl", "ZL", "pad--trigger")}
          {pad("l", "L", "pad--bumper")}
        </div>
        <div className="joycon__shell">
          <div className="joycon__system joycon__system--end">{pad("minus", <Icon name="minus" />, "pad--system")}</div>
          <StickPad
            stick="left"
            switchId={switchId}
            enabled={enabled}
            enqueue={enqueue}
            onMove={(stick, x, y) => inputListener.current?.({ kind: "stick", stick, x, y })}
            clickButton={pad("leftStick", "L3", "pad--click")}
          />
          <div className="dpad">
            <span className="dpad__hub" aria-hidden="true" />
            {pad("dpadUp", <Icon name="chevron" />, "dpad__arm dpad__arm--up")}
            {pad("dpadLeft", <Icon name="chevron" />, "dpad__arm dpad__arm--left")}
            {pad("dpadRight", <Icon name="chevron" />, "dpad__arm dpad__arm--right")}
            {pad("dpadDown", <Icon name="chevron" />, "dpad__arm dpad__arm--down")}
          </div>
          <div className="joycon__system joycon__system--end">{pad("capture", <Icon name="capture" />, "pad--system")}</div>
        </div>
      </section>
      <section className="joycon joycon--right" aria-label="Right Joy-Con">
        <div className="joycon__shoulders">
          {pad("r", "R", "pad--bumper")}
          {pad("zr", "ZR", "pad--trigger")}
        </div>
        <div className="joycon__shell">
          <div className="joycon__system">{pad("plus", <Icon name="plus" />, "pad--system")}</div>
          <div className="face">
            {pad("x", "X", "face__button face__button--x")}
            {pad("y", "Y", "face__button face__button--y")}
            {pad("a", "A", "face__button face__button--a")}
            {pad("b", "B", "face__button face__button--b")}
          </div>
          <StickPad
            stick="right"
            switchId={switchId}
            enabled={enabled}
            enqueue={enqueue}
            onMove={(stick, x, y) => inputListener.current?.({ kind: "stick", stick, x, y })}
            clickButton={pad("rightStick", "R3", "pad--click")}
          />
          <div className="joycon__system">{pad("home", <Icon name="home" />, "pad--system pad--home")}</div>
        </div>
      </section>
    </div>
  );
}

export const BUTTON_NAMES: Record<Button, string> = {
  a: "A",
  b: "B",
  x: "X",
  y: "Y",
  l: "L",
  r: "R",
  zl: "ZL",
  zr: "ZR",
  plus: "Plus",
  minus: "Minus",
  dpadUp: "D-pad up",
  dpadDown: "D-pad down",
  dpadLeft: "D-pad left",
  dpadRight: "D-pad right",
  home: "Home",
  capture: "Capture",
  leftStick: "Left stick click",
  rightStick: "Right stick click",
};

type IconName = "minus" | "plus" | "home" | "capture" | "chevron";

/** Small authored icon set: 20px grid, 1.75 stroke, round caps. */
function Icon({ name }: { name: IconName }) {
  const common = {
    width: 18,
    height: 18,
    viewBox: "0 0 20 20",
    fill: "none",
    stroke: "currentColor",
    strokeWidth: 1.75,
    strokeLinecap: "round" as const,
    strokeLinejoin: "round" as const,
    "aria-hidden": true,
  };
  switch (name) {
    case "minus":
      return (
        <svg {...common}>
          <path d="M5 10h10" />
        </svg>
      );
    case "plus":
      return (
        <svg {...common}>
          <path d="M10 5v10M5 10h10" />
        </svg>
      );
    case "home":
      return (
        <svg {...common}>
          <path d="M3.5 9.2 10 4l6.5 5.2" />
          <path d="M5.5 8v7.5h9V8" />
        </svg>
      );
    case "capture":
      return (
        <svg {...common}>
          <rect x="4" y="4" width="12" height="12" rx="2.5" />
          <circle cx="10" cy="10" r="2.6" fill="currentColor" stroke="none" />
        </svg>
      );
    case "chevron":
      return (
        <svg {...common} width={16} height={16}>
          <path d="M6 12.5 10 8l4 4.5" />
        </svg>
      );
  }
}

interface PadButtonProps {
  button: Button;
  label: ReactNode;
  className: string;
  active: boolean;
  disabled: boolean;
  onDown: (b: Button) => void;
  onUp: (b: Button) => void;
}

function PadButton({ button, label, className, active, disabled, onDown, onUp }: PadButtonProps) {
  const start = (e: ReactPointerEvent<HTMLButtonElement>) => {
    e.preventDefault();
    e.currentTarget.setPointerCapture(e.pointerId);
    onDown(button);
  };
  const end = () => onUp(button);
  return (
    <button
      type="button"
      className={`pad ${className} ${active ? "pad--active" : ""}`}
      aria-label={BUTTON_NAMES[button]}
      aria-pressed={active}
      disabled={disabled}
      onPointerDown={start}
      onPointerUp={end}
      onPointerCancel={end}
      onLostPointerCapture={end}
      onContextMenu={(e) => e.preventDefault()}
      onKeyDown={(e) => {
        if ((e.key === "Enter" || e.key === " ") && !e.repeat) onDown(button);
      }}
      onKeyUp={(e) => {
        if (e.key === "Enter" || e.key === " ") onUp(button);
      }}
    >
      {label}
    </button>
  );
}

interface StickPadProps {
  stick: Stick;
  switchId: string;
  enabled: boolean;
  enqueue: (key: string, action: () => Promise<unknown>) => Promise<unknown>;
  clickButton: React.ReactNode;
  onMove: (stick: Stick, x: number, y: number) => void;
}

/** Drag inside the ring to move the stick; lift to recentre. Updates are throttled, latest wins. */
function StickPad({ stick, switchId, enabled, enqueue, clickButton, onMove }: StickPadProps) {
  const moveListener = useRef(onMove);
  moveListener.current = onMove;
  const ring = useRef<HTMLDivElement>(null);
  const [knob, setKnob] = useState({ x: 0, y: 0 });
  const [dragging, setDragging] = useState(false);
  const latest = useRef({ x: 0, y: 0 });
  const sent = useRef({ x: 0, y: 0 });
  const timer = useRef<number | undefined>(undefined);
  const lastSendAt = useRef(0);

  const flush = useCallback(() => {
    timer.current = undefined;
    const { x, y } = latest.current;
    if (x === sent.current.x && y === sent.current.y && performance.now() - lastSendAt.current < 4_000) return;
    const moved = x !== sent.current.x || y !== sent.current.y;
    sent.current = { x, y };
    lastSendAt.current = performance.now();
    if (moved) moveListener.current(stick, x, y);
    enqueue(`stick-${stick}`, () => api.stick(switchId, stick, x, y, HOLD_LIMIT_MS));
  }, [enqueue, stick, switchId]);

  const schedule = useCallback(() => {
    if (timer.current === undefined) timer.current = window.setTimeout(flush, STICK_INTERVAL_MS);
  }, [flush]);

  // Keep a held stick alive against the server watchdog.
  useEffect(() => {
    const keepAlive = window.setInterval(() => {
      if (latest.current.x !== 0 || latest.current.y !== 0) flush();
    }, HOLD_REFRESH_MS);
    return () => window.clearInterval(keepAlive);
  }, [flush]);

  const move = (e: ReactPointerEvent<HTMLDivElement>) => {
    const el = ring.current;
    if (!el || !el.hasPointerCapture(e.pointerId)) return;
    const rect = el.getBoundingClientRect();
    const radius = rect.width / 2;
    let dx = (e.clientX - (rect.left + radius)) / radius;
    let dy = (e.clientY - (rect.top + radius)) / radius;
    const length = Math.hypot(dx, dy);
    if (length > 1) {
      dx /= length;
      dy /= length;
    }
    const round = (v: number) => Math.round(v * 100) / 100;
    latest.current = { x: round(dx), y: round(-dy) };
    setKnob({ x: dx, y: dy });
    schedule();
  };

  const release = () => {
    setDragging(false);
    window.clearTimeout(timer.current);
    timer.current = undefined;
    latest.current = { x: 0, y: 0 };
    setKnob({ x: 0, y: 0 });
    flush();
  };

  return (
    <div className="stick">
      <div
        ref={ring}
        className={`stick__well ${dragging ? "stick__well--active" : ""}`}
        role="slider"
        aria-label={`${stick === "left" ? "Left" : "Right"} stick`}
        aria-valuetext={`x ${latest.current.x}, y ${latest.current.y}`}
        aria-disabled={!enabled}
        onPointerDown={(e) => {
          if (!enabled) return;
          e.preventDefault();
          e.currentTarget.setPointerCapture(e.pointerId);
          setDragging(true);
          move(e);
        }}
        onPointerMove={move}
        onPointerUp={release}
        onPointerCancel={release}
      >
        <span className="stick__guides" aria-hidden="true" />
        <span className="stick__knob" style={{ "--x": knob.x, "--y": knob.y } as React.CSSProperties} aria-hidden="true" />
      </div>
      <div className="stick__foot">
        {clickButton}
        <output className="stick__readout" aria-hidden="true">
          {dragging ? `${formatAxis(latest.current.x)} ${formatAxis(latest.current.y)}` : ""}
        </output>
      </div>
    </div>
  );
}

function formatAxis(value: number): string {
  const sign = value > 0 ? "+" : value < 0 ? "−" : "\u2007";
  return `${sign}${Math.abs(value).toFixed(2)}`;
}
