import { useEffect, useRef, useState } from "react";
import { currentToken } from "./api";

export interface HubEvent {
  type: string;
  switchId: string | null;
  timestamp: string;
  data: unknown;
}

export type LinkState = "connecting" | "live" | "offline" | "unauthorised";

/**
 * Subscribes to /api/events. Authenticates with the first message (tokens never go in URLs),
 * and reconnects with backoff when the link drops.
 */
export function useHubEvents(onEvent: (event: HubEvent) => void, enabled: boolean): LinkState {
  const [state, setState] = useState<LinkState>("connecting");
  const handler = useRef(onEvent);
  handler.current = onEvent;

  useEffect(() => {
    if (!enabled) return;
    let socket: WebSocket | null = null;
    let retry = 0;
    let timer: number | undefined;
    let stopped = false;

    const open = () => {
      setState("connecting");
      // Relative to the page, so it also works when NX Hub is served under a sub-path.
      const url = new URL("api/events", document.baseURI);
      url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
      socket = new WebSocket(url);
      socket.onopen = () => {
        const token = currentToken();
        socket?.send(JSON.stringify({ type: "auth", token: token ?? "" }));
      };
      socket.onmessage = (message) => {
        try {
          const event = JSON.parse(String(message.data)) as HubEvent;
          if (event.type === "hello") {
            retry = 0;
            setState("live");
            return;
          }
          handler.current(event);
        } catch {
          // Ignore malformed frames.
        }
      };
      socket.onclose = (close) => {
        socket = null;
        if (stopped) return;
        if (close.code === 1008) {
          setState("unauthorised");
          return;
        }
        setState("offline");
        retry = Math.min(retry + 1, 6);
        timer = window.setTimeout(open, Math.min(30_000, 500 * 2 ** retry));
      };
    };

    open();
    return () => {
      stopped = true;
      window.clearTimeout(timer);
      socket?.close();
    };
  }, [enabled]);

  return state;
}
