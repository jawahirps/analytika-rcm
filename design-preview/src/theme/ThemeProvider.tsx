import {
  createContext,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react";

type Theme = "light" | "dark";
type Lang = "en" | "ar";

interface ThemeCtx {
  theme: Theme;
  lang: Lang;
  dir: "ltr" | "rtl";
  toggleTheme: () => void;
  setLang: (l: Lang) => void;
}

const Ctx = createContext<ThemeCtx | null>(null);

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setTheme] = useState<Theme>(
    () => (localStorage.getItem("preview-theme") as Theme) || "light"
  );
  const [lang, setLangState] = useState<Lang>(
    () => (localStorage.getItem("preview-dir") === "rtl" ? "ar" : "en")
  );
  const dir = lang === "ar" ? "rtl" : "ltr";

  useEffect(() => {
    const el = document.documentElement;
    el.setAttribute("data-theme", theme);
    el.classList.toggle("dark", theme === "dark");
    localStorage.setItem("preview-theme", theme);
  }, [theme]);

  useEffect(() => {
    const el = document.documentElement;
    el.setAttribute("dir", dir);
    el.setAttribute("lang", lang);
    localStorage.setItem("preview-dir", dir);
  }, [dir, lang]);

  return (
    <Ctx.Provider
      value={{
        theme,
        lang,
        dir,
        toggleTheme: () => setTheme((t) => (t === "light" ? "dark" : "light")),
        setLang: (l) => setLangState(l),
      }}
    >
      {children}
    </Ctx.Provider>
  );
}

export function useTheme() {
  const c = useContext(Ctx);
  if (!c) throw new Error("useTheme must be used within ThemeProvider");
  return c;
}
