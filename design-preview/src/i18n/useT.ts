import { useTheme } from "../theme/ThemeProvider";

// UI-chrome labels only (not data). Synthetic preview — no localized PHI.
const dict: Record<string, { en: string; ar: string }> = {
  dashboard: { en: "Dashboard", ar: "لوحة التحكم" },
  portal: { en: "Portal", ar: "البوابة" },
  reports: { en: "BI Reports", ar: "التقارير" },
  resubmission: { en: "Resubmission", ar: "إعادة التقديم" },
  admin: { en: "Admin", ar: "الإدارة" },
  sync_fetch: { en: "Sync & Fetch", ar: "المزامنة" },
  portal_files: { en: "Portal Files", ar: "ملفات البوابة" },
  claim_extracts: { en: "Claim Extracts", ar: "مستخلصات المطالبات" },
  denials: { en: "Denial Dashboard", ar: "لوحة الرفض" },
  workload: { en: "Workload", ar: "عبء العمل" },
  facility_status: { en: "Facility Status", ar: "حالة المنشآت" },
  search: { en: "Search", ar: "بحث" },
  apply: { en: "Apply", ar: "تطبيق" },
  connected: { en: "Connected", ar: "متصل" },
  degraded: { en: "Degraded", ar: "متدهور" },
  disconnected: { en: "Disconnected", ar: "غير متصل" },
  notifications: { en: "Notifications", ar: "الإشعارات" },
  sign_out: { en: "Sign out", ar: "تسجيل الخروج" },
  preview_banner: {
    en: "Design preview — synthetic data, not the live system",
    ar: "معاينة التصميم — بيانات تجريبية، ليست النظام الفعلي",
  },
};

export function useT() {
  const { lang } = useTheme();
  return (key: string) => dict[key]?.[lang] ?? key;
}
