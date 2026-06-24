import { MOBILE_NAV } from "@/data/navigation";

export function MobileNav({ active, onSelect }) {
  return (
    <nav className="mobile-bottom" aria-label="Mobile navigation">
      {MOBILE_NAV.map(({ label, target }) => (
        <button
          key={label}
          type="button"
          className={active === target ? "active" : ""}
          aria-current={active === target ? "page" : undefined}
          onClick={() => onSelect(target)}
        >
          {label}
        </button>
      ))}
    </nav>
  );
}
