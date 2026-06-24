import { useCallback, useEffect, useRef, useState } from "react";

export function useToast(duration = 2400) {
  const [message, setMessage] = useState("");
  const timerRef = useRef(null);

  const show = useCallback(
    (text) => {
      setMessage(text);
      if (timerRef.current) window.clearTimeout(timerRef.current);
      timerRef.current = window.setTimeout(() => setMessage(""), duration);
    },
    [duration],
  );

  useEffect(
    () => () => {
      if (timerRef.current) window.clearTimeout(timerRef.current);
    },
    [],
  );

  return { message, show };
}
