import { Ionicons, MaterialCommunityIcons } from "@expo/vector-icons";
import { StatusBar } from "expo-status-bar";
import React, { useMemo, useState } from "react";
import {
  Pressable,
  SafeAreaView,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  useWindowDimensions,
  View
} from "react-native";

type Screen = "login" | "facility" | "rcm";
type RcmTab = "Submissions" | "Resubmissions" | "Remittance" | "Denials" | "Clinicians" | "Operations" | "Insurance" | "Department";
type FacilityStatus = "connected" | "notSynced" | "missing";

const colors = {
  navy: "#011C40",
  ink: "#08233F",
  blue: "#285F84",
  teal: "#3FA9B9",
  aqua: "#9BE8EF",
  cloud: "#EAF4FB",
  surface: "#FFFFFF",
  grid: "#D8E8F3",
  muted: "#5A7287",
  green: "#11845B",
  red: "#D64242",
  gold: "#D6A43D"
};

const rcmTabs: { key: RcmTab; icon: keyof typeof Ionicons.glyphMap }[] = [
  { key: "Submissions", icon: "paper-plane" },
  { key: "Resubmissions", icon: "refresh" },
  { key: "Remittance", icon: "cash" },
  { key: "Denials", icon: "ban" },
  { key: "Clinicians", icon: "person" },
  { key: "Operations", icon: "settings" },
  { key: "Insurance", icon: "shield-checkmark" },
  { key: "Department", icon: "business" }
];

const facilities: { name: string; portal?: string; records: number; files: number; claims: number; status: FacilityStatus; lastSync?: string }[] = [
  { name: "Noor Al Shifa Medical Center UAQ", portal: "DHA · RHA", records: 5022, files: 0, claims: 0, status: "connected", lastSync: "22 May 2026 11:19" },
  { name: "Noor Al Shifa Medical Center Ajman", portal: "DHA · RHA", records: 12204, files: 0, claims: 0, status: "notSynced" },
  { name: "Alnoor Abu Dhabi", records: 0, files: 0, claims: 0, status: "missing" },
  { name: "Alnoor Deira", records: 0, files: 0, claims: 0, status: "missing" },
  { name: "Alnoor Rashidiya", records: 0, files: 0, claims: 0, status: "missing" }
];

const metricSeed: Record<RcmTab, number> = {
  Submissions: 86,
  Resubmissions: 64,
  Remittance: 78,
  Denials: 52,
  Clinicians: 71,
  Operations: 83,
  Insurance: 67,
  Department: 75
};

export default function App() {
  const [screen, setScreen] = useState<Screen>("login");
  const [tab, setTab] = useState<RcmTab>("Submissions");
  const [sectionsCollapsed, setSectionsCollapsed] = useState(false);
  const { width } = useWindowDimensions();
  const compact = width < 760;

  return (
    <SafeAreaView style={styles.safe}>
      <StatusBar style="dark" />
      {screen !== "login" && (
        <TopBar
          active={screen}
          onOpenFacility={() => setScreen("facility")}
          onOpenRcm={() => setScreen("rcm")}
          onLogout={() => setScreen("login")}
        />
      )}
      {screen === "login" ? (
        <LoginScreen onLogin={() => setScreen("facility")} />
      ) : screen === "facility" ? (
        <FacilityDashboard onOpenRcm={() => setScreen("rcm")} />
      ) : (
        <RcmDashboard
          tab={tab}
          onTabChange={setTab}
          collapsed={sectionsCollapsed}
          onToggleCollapsed={() => setSectionsCollapsed((value) => !value)}
          compact={compact}
        />
      )}
    </SafeAreaView>
  );
}

function TopBar({ active, onOpenFacility, onOpenRcm, onLogout }: { active: Screen; onOpenFacility: () => void; onOpenRcm: () => void; onLogout: () => void }) {
  return (
    <View style={styles.topBar}>
      <View style={styles.brandMark}>
        <MaterialCommunityIcons name="chart-line" size={21} color={colors.teal} />
      </View>
      <View style={styles.brandTextWrap}>
        <Text style={styles.brandTitle}>Ghaf Bi</Text>
        <Text style={styles.brandSub}>Business Intelligence</Text>
      </View>
      <View style={styles.topActions}>
        <Pill active={active === "facility"} label="Facilities" onPress={onOpenFacility} />
        <Pill active={active === "rcm"} label="RCM" onPress={onOpenRcm} />
        <Pressable accessibilityRole="button" onPress={onLogout} style={styles.iconButton}>
          <Ionicons name="log-out-outline" size={18} color={colors.navy} />
        </Pressable>
      </View>
    </View>
  );
}

function LoginScreen({ onLogin }: { onLogin: () => void }) {
  return (
    <ScrollView contentContainerStyle={styles.loginPage}>
      <View style={styles.loginHero}>
        <View style={styles.heroBadge}>
          <View style={styles.liveDot} />
          <Text style={styles.heroBadgeText}>Ghaf Business Intelligence</Text>
        </View>
        <Text style={styles.loginTitle}>Healthcare revenue intelligence, rebuilt for mobile.</Text>
        <Text style={styles.loginBody}>Track portal sync, claims, remittance, denials, and facility health from a native workspace.</Text>
        <View style={styles.heroMetricGrid}>
          <HeroMetric label="Facilities" value="14" detail="DHA & RHA" />
          <HeroMetric label="Daily activity" value="Live" detail="Fetch and review" />
        </View>
      </View>

      <View style={styles.authCard}>
        <Text style={styles.cardKicker}>Welcome back</Text>
        <Text style={styles.authTitle}>Access the dashboard</Text>
        <Text style={styles.authSubtitle}>Sign in to your Ghaf Business Intelligence account</Text>
        <Text style={styles.inputLabel}>Email address</Text>
        <TextInput style={styles.input} keyboardType="email-address" autoCapitalize="none" defaultValue="admin@ghafbi.ae" />
        <Text style={styles.inputLabel}>Password</Text>
        <TextInput style={styles.input} secureTextEntry defaultValue="Admin@123" />
        <Pressable accessibilityRole="button" onPress={onLogin} style={styles.primaryButton}>
          <Text style={styles.primaryButtonText}>Sign In</Text>
          <Ionicons name="arrow-forward" size={16} color={colors.navy} />
        </Pressable>
      </View>
    </ScrollView>
  );
}

function FacilityDashboard({ onOpenRcm }: { onOpenRcm: () => void }) {
  const totals = useMemo(() => ({
    connected: facilities.filter((f) => f.status === "connected").length,
    degraded: facilities.filter((f) => f.status === "notSynced").length,
    missing: facilities.filter((f) => f.status === "missing").length,
    records: facilities.reduce((sum, f) => sum + f.records, 0)
  }), []);

  return (
    <ScrollView contentContainerStyle={styles.page}>
      <View style={styles.pageHero}>
        <Text style={styles.cardKicker}>Operations overview</Text>
        <Text style={styles.pageTitle}>Facility Status</Text>
        <Text style={styles.pageDescription}>Live connectivity and sync health across registered facilities.</Text>
        <View style={styles.heroButtonRow}>
          <Pressable style={styles.secondaryButton}>
            <Ionicons name="sync" size={16} color={colors.navy} />
            <Text style={styles.secondaryButtonText}>Sync Portal</Text>
          </Pressable>
          <Pressable style={styles.secondaryButton} onPress={onOpenRcm}>
            <Ionicons name="analytics" size={16} color={colors.navy} />
            <Text style={styles.secondaryButtonText}>BI Reports</Text>
          </Pressable>
        </View>
      </View>

      <View style={styles.summaryGrid}>
        <SummaryCard icon="checkmark-circle" label="Connected" value={totals.connected.toString()} tone="green" />
        <SummaryCard icon="alert-circle" label="Not synced" value={totals.degraded.toString()} tone="gold" />
        <SummaryCard icon="close-circle" label="Missing" value={totals.missing.toString()} tone="red" />
        <SummaryCard icon="document-text" label="Total records" value={totals.records.toLocaleString()} tone="blue" />
      </View>

      <View style={styles.facilityList}>
        {facilities.map((facility) => <FacilityCard key={facility.name} facility={facility} />)}
      </View>
    </ScrollView>
  );
}

function RcmDashboard({ tab, onTabChange, collapsed, onToggleCollapsed, compact }: { tab: RcmTab; onTabChange: (tab: RcmTab) => void; collapsed: boolean; onToggleCollapsed: () => void; compact: boolean }) {
  const seed = metricSeed[tab];
  const trend = [seed - 18, seed - 10, seed - 4, seed + 3, seed + 8, seed + 12];

  return (
    <View style={styles.rcmScreen}>
      {!compact && (
        <View style={[styles.sideRail, collapsed && styles.sideRailCollapsed]}>
          <Pressable accessibilityRole="button" onPress={onToggleCollapsed} style={styles.collapseButton}>
            <Ionicons name={collapsed ? "chevron-forward" : "chevron-back"} size={20} color={colors.navy} />
          </Pressable>
          {rcmTabs.map((item) => (
            <Pressable key={item.key} onPress={() => onTabChange(item.key)} style={[styles.railItem, tab === item.key && styles.railItemActive, collapsed && styles.railItemCollapsed]}>
              <Ionicons name={item.icon} size={18} color={tab === item.key ? colors.navy : colors.blue} />
              {!collapsed && <Text style={[styles.railText, tab === item.key && styles.railTextActive]}>{item.key}</Text>}
            </Pressable>
          ))}
        </View>
      )}

      <ScrollView contentContainerStyle={styles.rcmContent}>
        {compact && (
          <ScrollView horizontal showsHorizontalScrollIndicator={false} style={styles.mobileTabs}>
            {rcmTabs.map((item) => (
              <Pressable key={item.key} onPress={() => onTabChange(item.key)} style={[styles.mobileTab, tab === item.key && styles.mobileTabActive]}>
                <Ionicons name={item.icon} size={16} color={tab === item.key ? colors.navy : colors.blue} />
                <Text style={styles.mobileTabText}>{item.key}</Text>
              </Pressable>
            ))}
          </ScrollView>
        )}

        <View style={styles.rcmHeader}>
          <View style={{ flex: 1 }}>
            <Text style={styles.pageTitle}>{tab}</Text>
            <Text style={styles.pageDescription}>Claim submission volumes, acceptance rates, and timelines</Text>
            <View style={styles.stableField}>
              <Ionicons name="calendar" size={16} color={colors.teal} />
              <Text style={styles.stableFieldText}>Stable field: Encounter Date</Text>
            </View>
          </View>
          <Text style={styles.updatedPill}>Updated 22 May 2026 15:30</Text>
        </View>

        <View style={styles.metricGrid}>
          <MetricCard icon="document-text" label="Total Claims" value={(seed * 124).toLocaleString()} delta="+8.4%" />
          <MetricCard icon="layers" label="Net Value" value={`AED ${seed * 18}K`} delta="+5.1%" />
          <MetricCard icon="checkmark-circle" label="Clean Rate" value={`${Math.min(seed + 9, 96)}%`} delta="+2.7%" />
          <MetricCard icon="time" label="TAT" value={`${Math.max(2, 14 - (seed % 9))} days`} delta="-1.3d" />
        </View>

        <View style={styles.panel}>
          <Text style={styles.panelTitle}>Performance Trend</Text>
          <Text style={styles.panelCopy}>Claim submission volumes are stable with Encounter Date as the shared timeline field across exports.</Text>
          <View style={styles.trendRow}>
            {trend.map((value, index) => <TrendColumn key={index} value={value} label={["Jan", "Feb", "Mar", "Apr", "May", "Jun"][index]} />)}
          </View>
        </View>
      </ScrollView>
    </View>
  );
}

function HeroMetric({ label, value, detail }: { label: string; value: string; detail: string }) {
  return (
    <View style={styles.heroMetric}>
      <Text style={styles.heroMetricLabel}>{label}</Text>
      <Text style={styles.heroMetricValue}>{value}</Text>
      <Text style={styles.heroMetricDetail}>{detail}</Text>
    </View>
  );
}

function SummaryCard({ icon, label, value, tone }: { icon: keyof typeof Ionicons.glyphMap; label: string; value: string; tone: "green" | "gold" | "red" | "blue" }) {
  return (
    <View style={styles.summaryCard}>
      <View style={[styles.summaryIcon, toneStyles[tone]]}>
        <Ionicons name={icon} size={19} color={toneColor[tone]} />
      </View>
      <View>
        <Text style={styles.summaryValue}>{value}</Text>
        <Text style={styles.summaryLabel}>{label}</Text>
      </View>
    </View>
  );
}

function FacilityCard({ facility }: { facility: (typeof facilities)[number] }) {
  const status = statusMeta[facility.status];
  return (
    <View style={[styles.facilityCard, { borderLeftColor: status.color }]}>
      <View style={styles.facilityHeader}>
        <View style={{ flex: 1 }}>
          <Text style={styles.facilityName}>{facility.name}</Text>
          {!!facility.portal && <Text style={styles.portalChip}>{facility.portal}</Text>}
        </View>
        <View style={styles.statusWrap}>
          <View style={[styles.statusDot, { backgroundColor: status.color }]} />
          <Text style={[styles.statusText, { color: status.color }]}>{status.label}</Text>
        </View>
      </View>
      <Text style={styles.syncText}>{facility.lastSync ? `Last sync: ${facility.lastSync}` : "Never synced"}</Text>
      <View style={styles.facilityStats}>
        <Stat label="Records" value={facility.records.toLocaleString()} />
        <Stat label="Claims" value={facility.claims.toLocaleString()} />
        <Stat label="Files" value={facility.files.toLocaleString()} />
      </View>
    </View>
  );
}

function MetricCard({ icon, label, value, delta }: { icon: keyof typeof Ionicons.glyphMap; label: string; value: string; delta: string }) {
  return (
    <View style={styles.metricCard}>
      <View style={styles.metricIcon}><Ionicons name={icon} size={18} color={colors.teal} /></View>
      <View>
        <Text style={styles.metricLabel}>{label}</Text>
        <Text style={styles.metricValue}>{value}</Text>
        <Text style={styles.metricDelta}>{delta}</Text>
      </View>
    </View>
  );
}

function TrendColumn({ value, label }: { value: number; label: string }) {
  return (
    <View style={styles.trendColumn}>
      <Text style={styles.trendValue}>{value}%</Text>
      <View style={[styles.trendBar, { height: Math.max(36, value * 1.75) }]} />
      <Text style={styles.trendLabel}>{label}</Text>
    </View>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.statBlock}>
      <Text style={styles.statValue}>{value}</Text>
      <Text style={styles.statLabel}>{label}</Text>
    </View>
  );
}

function Pill({ label, active, onPress }: { label: string; active: boolean; onPress: () => void }) {
  return (
    <Pressable onPress={onPress} style={[styles.topPill, active && styles.topPillActive]}>
      <Text style={[styles.topPillText, active && styles.topPillTextActive]}>{label}</Text>
    </Pressable>
  );
}

const statusMeta = {
  connected: { label: "Connected", color: colors.green },
  notSynced: { label: "Not synced", color: colors.gold },
  missing: { label: "No credential", color: colors.red }
};

const toneColor = {
  green: colors.green,
  gold: colors.gold,
  red: colors.red,
  blue: colors.teal
};

const toneStyles = StyleSheet.create({
  green: { backgroundColor: "#E8F7F0" },
  gold: { backgroundColor: "#FFF6DD" },
  red: { backgroundColor: "#FFECEC" },
  blue: { backgroundColor: colors.cloud }
});

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: "#F5F9FC" },
  loginPage: { flexGrow: 1, padding: 16, backgroundColor: colors.navy },
  loginHero: { padding: 22, borderRadius: 14, backgroundColor: "#06294E", gap: 14 },
  heroBadge: { alignSelf: "flex-start", flexDirection: "row", alignItems: "center", gap: 8, paddingHorizontal: 10, paddingVertical: 7, borderRadius: 999, borderWidth: 1, borderColor: "rgba(155,232,239,0.20)" },
  liveDot: { width: 9, height: 9, borderRadius: 9, backgroundColor: colors.aqua },
  heroBadgeText: { color: colors.cloud, fontSize: 11, fontWeight: "800", letterSpacing: 1.2, textTransform: "uppercase" },
  loginTitle: { color: "#FFF", fontSize: 34, lineHeight: 36, fontWeight: "800", letterSpacing: -1.2 },
  loginBody: { color: "rgba(234,244,251,0.78)", fontSize: 15, lineHeight: 23 },
  heroMetricGrid: { flexDirection: "row", gap: 10 },
  heroMetric: { flex: 1, padding: 14, borderRadius: 10, borderWidth: 1, borderColor: "rgba(155,232,239,0.16)", backgroundColor: "rgba(255,255,255,0.05)" },
  heroMetricLabel: { color: "rgba(234,244,251,0.72)", fontSize: 10, fontWeight: "800", textTransform: "uppercase" },
  heroMetricValue: { color: "#FFF", fontSize: 24, fontWeight: "800", marginTop: 5 },
  heroMetricDetail: { color: colors.aqua, fontSize: 11, fontWeight: "700" },
  authCard: { marginTop: 14, padding: 18, borderRadius: 14, backgroundColor: colors.surface, gap: 10 },
  cardKicker: { alignSelf: "flex-start", paddingHorizontal: 10, paddingVertical: 6, borderRadius: 999, overflow: "hidden", backgroundColor: colors.cloud, color: colors.blue, fontSize: 11, fontWeight: "800", textTransform: "uppercase", letterSpacing: 1 },
  authTitle: { color: colors.navy, fontSize: 24, fontWeight: "800", letterSpacing: -0.6 },
  authSubtitle: { color: colors.muted, fontSize: 14, lineHeight: 20 },
  inputLabel: { color: colors.navy, fontSize: 12, fontWeight: "800", textTransform: "uppercase", marginTop: 4 },
  input: { minHeight: 46, borderRadius: 8, borderWidth: 1, borderColor: "#CFE0EB", paddingHorizontal: 12, color: colors.navy, backgroundColor: "#FBFDFF" },
  primaryButton: { marginTop: 8, minHeight: 48, borderRadius: 10, backgroundColor: colors.aqua, flexDirection: "row", alignItems: "center", justifyContent: "center", gap: 8 },
  primaryButtonText: { color: colors.navy, fontWeight: "800", fontSize: 15 },
  topBar: { minHeight: 64, flexDirection: "row", alignItems: "center", paddingHorizontal: 14, gap: 9, backgroundColor: "rgba(255,255,255,0.96)", borderBottomWidth: 1, borderBottomColor: "#DCEAF3" },
  brandMark: { width: 38, height: 38, borderRadius: 10, alignItems: "center", justifyContent: "center", borderWidth: 1, borderColor: "#CDE3EE" },
  brandTextWrap: { flex: 1, minWidth: 0 },
  brandTitle: { color: colors.navy, fontSize: 16, fontWeight: "800" },
  brandSub: { color: colors.blue, fontSize: 10, fontWeight: "800", textTransform: "uppercase", letterSpacing: 0.7 },
  topActions: { flexDirection: "row", alignItems: "center", gap: 6 },
  topPill: { paddingHorizontal: 10, paddingVertical: 7, borderRadius: 999, backgroundColor: colors.cloud },
  topPillActive: { backgroundColor: colors.navy },
  topPillText: { color: colors.blue, fontSize: 12, fontWeight: "800" },
  topPillTextActive: { color: "#FFF" },
  iconButton: { width: 36, height: 36, borderRadius: 8, alignItems: "center", justifyContent: "center", backgroundColor: colors.cloud },
  page: { padding: 14, gap: 12 },
  pageHero: { padding: 16, borderRadius: 14, backgroundColor: colors.surface, borderWidth: 1, borderColor: "#D5E5EF", gap: 9 },
  pageTitle: { color: colors.navy, fontSize: 22, fontWeight: "800", letterSpacing: -0.5 },
  pageDescription: { color: colors.blue, fontSize: 14, lineHeight: 21, fontWeight: "600" },
  heroButtonRow: { flexDirection: "row", gap: 8, flexWrap: "wrap" },
  secondaryButton: { minHeight: 42, flexGrow: 1, borderRadius: 10, borderWidth: 1, borderColor: "#BFD3E0", flexDirection: "row", alignItems: "center", justifyContent: "center", gap: 8 },
  secondaryButtonText: { color: colors.navy, fontSize: 13, fontWeight: "800" },
  summaryGrid: { flexDirection: "row", flexWrap: "wrap", gap: 10 },
  summaryCard: { flexBasis: "47.5%", flexGrow: 1, minHeight: 94, padding: 14, borderRadius: 14, backgroundColor: colors.surface, flexDirection: "row", alignItems: "center", gap: 12, borderWidth: 1, borderColor: "#DCEAF3" },
  summaryIcon: { width: 42, height: 42, borderRadius: 10, alignItems: "center", justifyContent: "center" },
  summaryValue: { color: colors.navy, fontSize: 26, fontWeight: "800" },
  summaryLabel: { color: colors.blue, fontSize: 11, fontWeight: "800", textTransform: "uppercase" },
  facilityList: { gap: 10 },
  facilityCard: { padding: 14, borderRadius: 14, backgroundColor: colors.surface, borderWidth: 1, borderColor: "#DCEAF3", borderLeftWidth: 4 },
  facilityHeader: { flexDirection: "row", alignItems: "flex-start", gap: 10 },
  facilityName: { color: colors.navy, fontSize: 15, fontWeight: "800", lineHeight: 20 },
  portalChip: { alignSelf: "flex-start", marginTop: 5, paddingHorizontal: 9, paddingVertical: 4, borderRadius: 999, overflow: "hidden", backgroundColor: colors.cloud, color: colors.blue, fontSize: 11, fontWeight: "800" },
  statusWrap: { flexDirection: "row", alignItems: "center", gap: 5, paddingTop: 2 },
  statusDot: { width: 8, height: 8, borderRadius: 8 },
  statusText: { fontSize: 11, fontWeight: "800", textTransform: "uppercase" },
  syncText: { color: colors.muted, marginTop: 12, fontSize: 12, fontWeight: "700" },
  facilityStats: { marginTop: 12, paddingTop: 12, borderTopWidth: 1, borderTopColor: "#E2EEF5", flexDirection: "row", justifyContent: "space-around" },
  statBlock: { alignItems: "center", flex: 1 },
  statValue: { color: colors.navy, fontSize: 18, fontWeight: "800" },
  statLabel: { color: colors.blue, fontSize: 10, fontWeight: "800", textTransform: "uppercase" },
  rcmScreen: { flex: 1, flexDirection: "row", backgroundColor: "#F1F7FB" },
  sideRail: { width: 238, padding: 12, gap: 8, borderRightWidth: 1, borderRightColor: "#D7E6F0", backgroundColor: "rgba(255,255,255,0.76)" },
  sideRailCollapsed: { width: 64, paddingHorizontal: 8 },
  collapseButton: { alignSelf: "flex-end", width: 40, height: 40, borderRadius: 10, backgroundColor: "#FFF", alignItems: "center", justifyContent: "center", borderWidth: 1, borderColor: "#D6E4EE" },
  railItem: { minHeight: 46, borderRadius: 10, flexDirection: "row", alignItems: "center", gap: 10, paddingHorizontal: 12 },
  railItemActive: { backgroundColor: "#FFF", borderLeftWidth: 3, borderLeftColor: colors.gold },
  railItemCollapsed: { justifyContent: "center", paddingHorizontal: 0 },
  railText: { color: colors.blue, fontSize: 14, fontWeight: "800" },
  railTextActive: { color: colors.navy },
  rcmContent: { padding: 14, gap: 12 },
  mobileTabs: { marginBottom: 2 },
  mobileTab: { marginRight: 8, paddingHorizontal: 12, minHeight: 40, borderRadius: 999, borderWidth: 1, borderColor: "#D7E6F0", backgroundColor: "#FFF", flexDirection: "row", alignItems: "center", gap: 6 },
  mobileTabActive: { backgroundColor: colors.cloud, borderColor: colors.teal },
  mobileTabText: { color: colors.navy, fontSize: 12, fontWeight: "800" },
  rcmHeader: { padding: 16, borderRadius: 14, backgroundColor: colors.surface, borderWidth: 1, borderColor: "#D7E6F0", gap: 12 },
  stableField: { alignSelf: "flex-start", marginTop: 10, flexDirection: "row", alignItems: "center", gap: 7, paddingHorizontal: 10, paddingVertical: 8, borderRadius: 999, backgroundColor: colors.cloud },
  stableFieldText: { color: colors.navy, fontSize: 12, fontWeight: "800" },
  updatedPill: { alignSelf: "flex-start", paddingHorizontal: 10, paddingVertical: 7, borderRadius: 999, overflow: "hidden", backgroundColor: colors.cloud, color: colors.navy, fontSize: 12, fontWeight: "800" },
  metricGrid: { flexDirection: "row", flexWrap: "wrap", gap: 10 },
  metricCard: { flexBasis: "47.5%", flexGrow: 1, minHeight: 96, padding: 14, borderRadius: 14, backgroundColor: colors.surface, borderWidth: 1, borderColor: "#DCEAF3", flexDirection: "row", alignItems: "center", gap: 12 },
  metricIcon: { width: 42, height: 42, borderRadius: 10, backgroundColor: colors.cloud, alignItems: "center", justifyContent: "center" },
  metricLabel: { color: colors.blue, fontSize: 11, fontWeight: "800", textTransform: "uppercase" },
  metricValue: { color: colors.navy, fontSize: 23, fontWeight: "800" },
  metricDelta: { color: colors.blue, fontSize: 12, fontWeight: "800" },
  panel: { padding: 16, borderRadius: 14, backgroundColor: colors.surface, borderWidth: 1, borderColor: "#DCEAF3" },
  panelTitle: { color: colors.navy, fontSize: 18, fontWeight: "800" },
  panelCopy: { marginTop: 10, color: colors.blue, fontSize: 14, lineHeight: 21, fontWeight: "600" },
  trendRow: { minHeight: 230, marginTop: 12, flexDirection: "row", alignItems: "flex-end", justifyContent: "space-between", gap: 8 },
  trendColumn: { flex: 1, alignItems: "center", gap: 7 },
  trendValue: { color: colors.navy, fontSize: 12, fontWeight: "800" },
  trendBar: { width: "100%", maxWidth: 42, borderRadius: 8, backgroundColor: colors.teal },
  trendLabel: { color: colors.blue, fontSize: 11, fontWeight: "800" }
});
