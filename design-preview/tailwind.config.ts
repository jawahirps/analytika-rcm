import type { Config } from "tailwindcss";

// Brand tokens reference CSS variables defined in src/theme/tokens.css so dark
// mode is a single class swap and charts can read the same values.
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  darkMode: "class",
  theme: {
    extend: {
      colors: {
        bg: "var(--bg)",
        surface: "var(--surface)",
        "surface-alt": "var(--surface-alt)",
        border: "var(--border)",
        ink: "var(--text)",
        muted: "var(--text-muted)",
        navy: "var(--navy)",
        teal: "var(--teal)",
        forest: "var(--forest)",
        gold: "var(--gold)",
        coral: "var(--coral)",
        mint: "var(--mint)",
        success: "var(--success)",
        warning: "var(--warning)",
        danger: "var(--danger)",
        info: "var(--info)",
        accent: "var(--accent)",
        accent2: "var(--accent2)",
      },
      fontFamily: {
        sans: ["Inter", "system-ui", "-apple-system", "Segoe UI", "sans-serif"],
      },
      borderRadius: {
        sm: "6px",
        DEFAULT: "8px",
        md: "10px",
        lg: "12px",
        xl: "16px",
        "2xl": "22px",
      },
      boxShadow: {
        card: "0 12px 34px rgba(15,39,66,.08)",
        soft: "0 1px 4px rgba(8,20,35,.06)",
      },
    },
  },
  plugins: [],
} satisfies Config;
