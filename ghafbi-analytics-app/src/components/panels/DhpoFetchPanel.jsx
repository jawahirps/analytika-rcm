import { useMemo, useState } from "react";
import { CheckCircle2, CloudDownload, Code2, DatabaseZap, FileDown, Search, ShieldCheck } from "lucide-react";
import { DHPO_ENDPOINTS, DHPO_FETCH_STEPS, DHPO_RESULT_CODES, DHPO_TRANSACTION_TYPES } from "@/data/dhpoSpec";
import {
  buildDownloadTransactionFileEnvelope,
  buildGetNewTransactionsEnvelope,
  buildSearchTransactionsEnvelope,
  buildSetTransactionDownloadedEnvelope,
  getSampleDhpoFiles,
} from "@/services/dhpoClient";
import { Panel, PanelHead } from "@/components/ui/Panel";

const INITIAL_FORM = {
  endpointType: "live",
  login: "",
  password: "",
  mode: "new",
  direction: "2",
  callerLicense: "",
  ePartner: "",
  transactionID: "-1",
  transactionStatus: "1",
  transactionFileName: "",
  transactionFromDate: "",
  transactionToDate: "",
  minRecordCount: "-1",
  maxRecordCount: "-1",
};

export function DhpoFetchPanel({ onAction }) {
  const [form, setForm] = useState(INITIAL_FORM);
  const [files, setFiles] = useState([]);
  const [selectedFileID, setSelectedFileID] = useState("");
  const [status, setStatus] = useState("Ready to fetch from DHPO.");
  const [previewMethod, setPreviewMethod] = useState("GetNewTransactions");

  const endpoint = DHPO_ENDPOINTS[form.endpointType];
  const selectedFile = files.find((file) => file.fileID === selectedFileID);

  const envelopePreview = useMemo(() => {
    const credentials = { login: form.login || "{{login}}", password: form.password ? "{{password}}" : "{{password}}" };
    if (previewMethod === "DownloadTransactionFile") {
      return buildDownloadTransactionFileEnvelope({ ...credentials, fileID: selectedFileID || "{{fileID}}" });
    }
    if (previewMethod === "SetTransactionDownloaded") {
      return buildSetTransactionDownloadedEnvelope({ ...credentials, fileID: selectedFileID || "{{fileID}}" });
    }
    if (form.mode === "search") {
      return buildSearchTransactionsEnvelope({
        ...credentials,
        direction: form.direction,
        callerLicense: form.callerLicense || "",
        ePartner: form.ePartner || "",
        transactionID: form.transactionID,
        transactionStatus: form.transactionStatus,
        transactionFileName: form.transactionFileName || "",
        transactionFromDate: form.transactionFromDate || "",
        transactionToDate: form.transactionToDate || "",
        minRecordCount: form.minRecordCount,
        maxRecordCount: form.maxRecordCount,
      });
    }
    return buildGetNewTransactionsEnvelope(credentials);
  }, [form, previewMethod, selectedFileID]);

  function update(field, value) {
    setForm((current) => ({ ...current, [field]: value }));
  }

  function loadSample() {
    const sampleFiles = getSampleDhpoFiles();
    setFiles(sampleFiles);
    setSelectedFileID(sampleFiles[0]?.fileID ?? "");
    setStatus("Loaded sample DHPO file-list XML from the specification format.");
    onAction?.("DHPO sample fetch");
  }

  function stageDownload() {
    if (!selectedFile) {
      setStatus("Select a DHPO FileID before staging download.");
      return;
    }
    setPreviewMethod("DownloadTransactionFile");
    setStatus(`DownloadTransactionFile staged for FileID ${selectedFile.fileID}.`);
    onAction?.("DHPO download staged");
  }

  function markDownloaded() {
    if (!selectedFile) {
      setStatus("Select a DHPO FileID before marking downloaded.");
      return;
    }
    setPreviewMethod("SetTransactionDownloaded");
    setFiles((current) =>
      current.map((file) => (file.fileID === selectedFile.fileID ? { ...file, isDownloaded: true } : file)),
    );
    setStatus(`SetTransactionDownloaded staged for FileID ${selectedFile.fileID}.`);
    onAction?.("DHPO mark downloaded staged");
  }

  return (
    <Panel className="dhpo-panel wide">
      <PanelHead compact title="DHPO Fetch" action={<DatabaseZap size={18} aria-hidden="true" />} />

      <div className="dhpo-layout">
        <section className="dhpo-config" aria-label="DHPO connection settings">
          <div className="dhpo-step-list">
            {DHPO_FETCH_STEPS.map((step, index) => (
              <div className="dhpo-step" key={step}>
                <span>{index + 1}</span>
                <p>{step}</p>
              </div>
            ))}
          </div>

          <div className="dhpo-form-grid">
            <label>
              Endpoint
              <select value={form.endpointType} onChange={(event) => update("endpointType", event.target.value)}>
                <option value="live">Live transactions</option>
                <option value="archive">Archive search/download</option>
              </select>
            </label>
            <label>
              Mode
              <select value={form.mode} onChange={(event) => update("mode", event.target.value)}>
                <option value="new">Get new transactions</option>
                <option value="search">Search transactions</option>
              </select>
            </label>
            <label>
              Login
              <input value={form.login} onChange={(event) => update("login", event.target.value)} placeholder="DHPO username" />
            </label>
            <label>
              Password
              <input type="password" value={form.password} onChange={(event) => update("password", event.target.value)} placeholder="Stored securely by backend" />
            </label>
          </div>

          {form.mode === "search" && (
            <div className="dhpo-search-grid">
              <label>
                Direction
                <select value={form.direction} onChange={(event) => update("direction", event.target.value)}>
                  <option value="1">Sent only</option>
                  <option value="2">Received only</option>
                </select>
              </label>
              <label>
                Transaction
                <select value={form.transactionID} onChange={(event) => update("transactionID", event.target.value)}>
                  {DHPO_TRANSACTION_TYPES.map((type) => (
                    <option key={type.value} value={type.value}>{type.label}</option>
                  ))}
                </select>
              </label>
              <label>
                Status
                <select value={form.transactionStatus} onChange={(event) => update("transactionStatus", event.target.value)}>
                  <option value="1">New only</option>
                  <option value="2">Already downloaded</option>
                </select>
              </label>
              <label>
                File name
                <input value={form.transactionFileName} onChange={(event) => update("transactionFileName", event.target.value)} placeholder="Any part of file name" />
              </label>
            </div>
          )}

          <div className="dhpo-actions">
            <button type="button" className="primary-btn" onClick={loadSample}>
              <CloudDownload size={16} aria-hidden="true" />
              Fetch New
            </button>
            <button type="button" className="toolbar-btn" onClick={() => setPreviewMethod(form.mode === "search" ? "SearchTransactions" : "GetNewTransactions")}>
              <Search size={16} aria-hidden="true" />
              Preview Request
            </button>
            <button type="button" className="toolbar-btn" onClick={stageDownload}>
              <FileDown size={16} aria-hidden="true" />
              Download File
            </button>
            <button type="button" className="toolbar-btn" onClick={markDownloaded}>
              <CheckCircle2 size={16} aria-hidden="true" />
              Mark Downloaded
            </button>
          </div>

          <p className="dhpo-status">{status}</p>
        </section>

        <section className="dhpo-results" aria-label="Fetched DHPO transactions">
          <div className="dhpo-result-head">
            <div>
              <strong>{files.length} files</strong>
              <span>{endpoint}</span>
            </div>
            <ShieldCheck size={18} aria-hidden="true" />
          </div>
          <div className="dhpo-file-list">
            {files.length === 0 ? (
              <div className="dhpo-empty">Use Fetch New to load DHPO file metadata.</div>
            ) : (
              files.map((file) => (
                <button
                  key={file.fileID}
                  type="button"
                  className={file.fileID === selectedFileID ? "selected" : ""}
                  onClick={() => {
                    setSelectedFileID(file.fileID);
                    setPreviewMethod("DownloadTransactionFile");
                  }}
                >
                  <span>{file.fileName}</span>
                  <em>{file.fileID} · {file.recordCount.toLocaleString()} records</em>
                  <strong className={file.isDownloaded ? "good" : "warn"}>{file.isDownloaded ? "Downloaded" : "New"}</strong>
                </button>
              ))
            )}
          </div>
        </section>
      </div>

      <details className="dhpo-code">
        <summary><Code2 size={16} aria-hidden="true" /> SOAP request preview</summary>
        <pre>{envelopePreview}</pre>
      </details>

      <div className="dhpo-codes">
        {Object.entries(DHPO_RESULT_CODES).slice(3, 8).map(([code, meaning]) => (
          <span key={code}><strong>{code}</strong>{meaning}</span>
        ))}
      </div>
    </Panel>
  );
}
