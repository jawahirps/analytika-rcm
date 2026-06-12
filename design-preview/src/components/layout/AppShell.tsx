import { useState } from "react";
import { Outlet } from "react-router-dom";
import { Sidebar } from "./Sidebar";
import { Topbar } from "./Topbar";
import { useT } from "../../i18n/useT";

export function AppShell() {
  const [open, setOpen] = useState(false);
  const t = useT();
  return (
    <div className="min-h-screen bg-bg">
      <Sidebar open={open} />
      {open && (
        <div
          className="fixed inset-0 z-20 bg-black/30 lg:hidden"
          onClick={() => setOpen(false)}
        />
      )}
      <div className="lg:ps-64">
        <Topbar onMenu={() => setOpen((o) => !o)} />
        <div className="bg-accent/10 px-4 py-1.5 text-center text-xs font-semibold text-accent">
          {t("preview_banner")}
        </div>
        <main className="p-4 lg:p-6">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
