import { useCallback, useEffect, useState } from "react";

const STORAGE_KEY = "ghafbi-theme";

function readTheme() {
  if (typeof window === "undefined") return "light";
  return document.documentElement.dataset.theme || localStorage.getItem(STORAGE_KEY) || "light";
}

export function useTheme() {
  const [theme, setThemeState] = useState(readTheme);

  const apply = useCallback((next) => {
    document.documentElement.dataset.theme = next;
    try {
      localStorage.setItem(STORAGE_KEY, next);
    } catch (_) {}
    setThemeState(next);
  }, []);

  useEffect(() => {
    apply(readTheme());
  }, [apply]);

  const toggle = useCallback(() => {
    setThemeState((current) => {
      const next = current === "dark" ? "light" : "dark";
      document.documentElement.dataset.theme = next;
      try {
        localStorage.setItem(STORAGE_KEY, next);
      } catch (_) {}
      return next;
    });
  }, []);

  return { theme, setTheme: apply, toggle };
}
