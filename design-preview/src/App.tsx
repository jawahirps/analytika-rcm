import { Navigate, Route, Routes } from "react-router-dom";
import { AppShell } from "./components/layout/AppShell";
import { FacilityStatus } from "./pages/Dashboard/FacilityStatus";
import { RCMDashboard } from "./pages/BIReports/RCMDashboard";
import { SyncFetch } from "./pages/Portal/SyncFetch";
import { PortalFiles } from "./pages/Portal/PortalFiles";
import { ClaimExtracts } from "./pages/Portal/ClaimExtracts";
import { DenialDashboard } from "./pages/Resubmission/DenialDashboard";
import { Workload } from "./pages/Resubmission/Workload";

export default function App() {
  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route index element={<Navigate to="/dashboard" replace />} />
        <Route path="/dashboard" element={<FacilityStatus />} />
        <Route path="/reports" element={<Navigate to="/reports/submissions" replace />} />
        <Route path="/reports/:tab" element={<RCMDashboard />} />
        <Route path="/portal/sync" element={<SyncFetch />} />
        <Route path="/portal/files" element={<PortalFiles />} />
        <Route path="/portal/extracts" element={<ClaimExtracts />} />
        <Route path="/resubmission/denials" element={<DenialDashboard />} />
        <Route path="/resubmission/workload" element={<Workload />} />
        <Route path="*" element={<Navigate to="/dashboard" replace />} />
      </Route>
    </Routes>
  );
}
