import { Bell, Menu, Moon, Sun, UserRound } from "lucide-react";
import { useTheme } from "../../theme/ThemeProvider";
import { useT } from "../../i18n/useT";

export function Topbar({ onMenu }: { onMenu: () => void }) {
  const { theme, toggleTheme, lang, setLang } = useTheme();
  const t = useT();
  return (
    <header className="sticky top-0 z-20 flex h-14 items-center gap-3 border-b border-border bg-surface/80 px-4 backdrop-blur">
      <button
        onClick={onMenu}
        className="grid h-9 w-9 place-items-center rounded-lg text-muted hover:bg-ink/[0.06] lg:hidden"
        aria-label="Menu"
      >
        <Menu size={18} />
      </button>

      <div className="ms-auto flex items-center gap-2">
        {/* EN / AR */}
        <div className="flex overflow-hidden rounded-lg border border-border text-xs font-bold">
          {(["en", "ar"] as const).map((l) => (
            <button
              key={l}
              onClick={() => setLang(l)}
              className={`px-2.5 py-1.5 ${
                lang === l ? "bg-navy text-white" : "text-muted hover:bg-ink/[0.05]"
              }`}
            >
              {l.toUpperCase()}
            </button>
          ))}
        </div>

        <button
          onClick={toggleTheme}
          className="inline-flex items-center gap-1.5 rounded-lg border border-border px-2.5 py-1.5 text-xs font-bold text-muted hover:bg-ink/[0.05]"
        >
          {theme === "dark" ? <Sun size={15} /> : <Moon size={15} />}
          {theme === "dark" ? "Light" : "Dark"}
        </button>

        <button
          className="relative grid h-9 w-9 place-items-center rounded-lg text-muted hover:bg-ink/[0.06]"
          aria-label={t("notifications")}
        >
          <Bell size={17} />
          <span className="absolute end-1.5 top-1.5 h-2 w-2 rounded-full bg-coral" />
        </button>

        <button className="flex items-center gap-2 rounded-lg border border-border px-2 py-1.5 text-sm font-semibold text-ink hover:bg-ink/[0.05]">
          <span className="grid h-6 w-6 place-items-center rounded-full bg-gradient-to-br from-teal to-forest text-white">
            <UserRound size={14} />
          </span>
          <span className="hidden sm:inline">admin</span>
        </button>
      </div>
    </header>
  );
}
