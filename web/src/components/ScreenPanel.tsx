import { useEffect, useState } from "react";
import { ApiError, api } from "../api";

interface Props {
  switchId: string;
  connected: boolean;
}

/**
 * Live view: repeatedly fetches the newest screenshot while switched on. Frames come from
 * sys-botbase's pixelPeek, so this is roughly one frame a second rather than video.
 */
export function ScreenPanel({ switchId, connected }: Props) {
  const [live, setLive] = useState(false);
  const [frame, setFrame] = useState<{ url: string; capturedAt: string; captureMs: number } | null>(null);
  const [fps, setFps] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  // Stop when the Switch changes or disconnects.
  useEffect(() => {
    setLive(false);
    setError(null);
  }, [switchId]);
  useEffect(() => {
    if (!connected) setLive(false);
  }, [connected]);

  useEffect(() => {
    if (!live) return;
    let stopped = false;
    const times: number[] = [];

    const loop = async () => {
      while (!stopped) {
        if (document.visibilityState === "hidden") {
          await sleep(500);
          continue;
        }

        try {
          setLoading(true);
          const next = await api.screen(switchId, 250);
          if (stopped) {
            URL.revokeObjectURL(next.url);
            break;
          }

          setFrame(next);
          setError(null);
          const now = performance.now();
          times.push(now);
          while (times.length > 0 && now - times[0]! > 5000) times.shift();
          setFps(times.length > 1 ? (times.length - 1) / ((now - times[0]!) / 1000) : null);
        } catch (e) {
          setError(e instanceof ApiError ? e.message : String(e));
          if (e instanceof ApiError && (e.code === "screenshot_unavailable" || e.status === 409 || e.status === 401)) {
            setLive(false);
            break;
          }

          await sleep(2000);
        } finally {
          setLoading(false);
        }

        await sleep(150);
      }
    };

    void loop();
    return () => {
      stopped = true;
    };
  }, [live, switchId]);

  // Free each frame's object URL once the next one has replaced it (or on unmount).
  useEffect(() => {
    const url = frame?.url;
    return () => {
      if (url) URL.revokeObjectURL(url);
    };
  }, [frame?.url]);

  return (
    <section className="card screen">
      <header className="card__head">
        <h3>
          Screen
          {live && fps !== null && <span className="muted"> · {fps.toFixed(1)} fps</span>}
        </h3>
        <button
          type="button"
          className={`btn btn--small ${live ? "btn--live" : ""}`}
          disabled={!connected}
          aria-pressed={live}
          onClick={() => setLive((v) => !v)}
        >
          {live ? "Stop live view" : "Live view"}
        </button>
      </header>
      <div className={`screen__frame ${live ? "screen__frame--live" : ""}`}>
        {frame ? (
          <img src={frame.url} alt="Switch screen" className="screen__image" />
        ) : (
          <p className="screen__placeholder muted">
            {connected ? "Start live view to see the Switch screen here." : "Connect the Switch to see its screen."}
          </p>
        )}
        {live && loading && !frame && <span className="screen__badge">Capturing…</span>}
      </div>
      {frame && (
        <p className="screen__meta muted">
          Captured {new Date(frame.capturedAt).toLocaleTimeString()} · {frame.captureMs.toFixed(0)} ms per capture
        </p>
      )}
      {error && <p className="notice notice--error">{error}</p>}
      {live && <p className="screen__hint muted">Button presses can lag slightly while a frame is being captured.</p>}
    </section>
  );
}

function sleep(ms: number) {
  return new Promise((resolve) => window.setTimeout(resolve, ms));
}
