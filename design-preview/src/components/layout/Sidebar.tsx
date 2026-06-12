import {
  ChevronDown,
  FileBarChart,
  FileText,
  Inbox,
  LayoutDashboard,
  RefreshCw,
  RotateCcw,
  ShieldCheck,
  type LucideIcon,
} from "lucide-react";
import { useState } from "react";
import { NavLink } from "react-router-dom";
import { useT } from "../../i18n/useT";

interface Item {
  to: string;
  label: string;
  icon: LucideIcon;
}
interface Section {
  key: string;
  label: string;
  icon: LucideIcon;
  items: Item[];
}

export function Sidebar({ open }: { open: boolean }) {
  const t = useT();
  const sections: Section[] = [
    {
      key: "portal",
      label: t("portal"),
      icon: RefreshCw,
      items: [
        { to: "/portal/sync", label: t("sync_fetch"), icon: RefreshCw },
        { to: "/portal/files", label: t("portal_files"), icon: Inbox },
        { to: "/portal/extracts", label: t("claim_extracts"), icon: FileText },
      ],
    },
    {
      key: "resub",
      label: t("resubmission"),
      icon: RotateCcw,
      items: [
        { to: "/resubmission/denials", label: t("denials"), icon: ShieldCheck },
        { to: "/resubmission/workload", label: t("workload"), icon: FileBarChart },
      ],
    },
  ];
  const [expanded, setExpanded] = useState<Record<string, boolean>>({
    portal: true,
    resub: true,
  });

  const linkBase =
    "flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors";
  const linkCls = ({ isActive }: { isActive: boolean }) =>
    `${linkBase} ${
      isActive
        ? "bg-accent/15 text-accent"
        : "text-muted hover:bg-ink/[0.05] hover:text-ink"
    }`;

  return (
    <aside
      className={`fixed inset-y-0 start-0 z-30 flex w-64 flex-col border-e border-border bg-surface transition-transform ${
        open ? "translate-x-0" : "-translate-x-full rtl:translate-x-full"
      } lg:translate-x-0`}
      style={{ ["--accent" as string]: "#14B8A6" }}
    >
      <div className="flex items-center gap-2.5 border-b border-border px-4 py-4">
        <div className="grid h-9 w-9 place-items-center rounded-lg bg-gradient-to-br from-teal to-forest text-white">
          <LayoutDashboard size={18} />
        </div>
        <div>
          <div className="text-sm font-extrabold leading-none text-ink">Analytika</div>
          <div className="text-[11px] font-medium text-muted">RCM Platform</div>
        </div>
      </div>

      <nav className="flex-1 space-y-1 overflow-y-auto p-3">
        <NavLink to="/dashboard" className={linkCls}>
          <LayoutDashboard size={17} />
          {t("dashboard")}
        </NavLink>

        {sections.map((s) => (
          <div key={s.key} className="pt-2">
            <button
              onClick={() =>
                setExpanded((e) => ({ ...e, [s.key]: !e[s.key] }))
              }
              className="flex w-full items-center gap-3 rounded-lg px-3 py-2 text-sm font-semibold text-ink hover:bg-ink/[0.05]"
            >
              <s.icon size={17} />
              {s.label}
              <ChevronDown
                size={15}
                className={`ms-auto transition-transform ${
                  expanded[s.key] ? "" : "-rotate-90"
                }`}
              />
            </button>
            {expanded[s.key] && (
              <div className="mt-1 space-y-1 ps-3">
                {s.items.map((it) => (
                  <NavLink key={it.to} to={it.to} className={linkCls}>
                    <it.icon size={16} />
                    {it.label}
                  </NavLink>
                ))}
              </div>
            )}
          </div>
        ))}

        <NavLink to="/reports/submissions" className={linkCls}>
          <FileBarChart size={17} />
          {t("reports")}
        </NavLink>
      </nav>

      <div className="border-t border-border px-4 py-3 text-[11px] text-muted">
        {t("preview_banner")}
      </div>
    </aside>
  );
}
