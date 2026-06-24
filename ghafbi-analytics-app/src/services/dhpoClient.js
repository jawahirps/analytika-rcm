import { DHPO_ENDPOINTS, SAMPLE_DHPO_FILES_XML } from "@/data/dhpoSpec";

const SOAP_NS = "http://tempuri.org/";

function escapeXml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&apos;");
}

function soapEnvelope(methodName, fields) {
  const body = Object.entries(fields)
    .map(([key, value]) => `<${key}>${escapeXml(value)}</${key}>`)
    .join("");

  return `<?xml version="1.0" encoding="utf-8"?>
<soap:Envelope xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
  <soap:Body>
    <${methodName} xmlns="${SOAP_NS}">
      ${body}
    </${methodName}>
  </soap:Body>
</soap:Envelope>`;
}

export function buildGetNewTransactionsEnvelope({ login, password }) {
  return soapEnvelope("GetNewTransactions", {
    login,
    pwd: password,
  });
}

export function buildDownloadTransactionFileEnvelope({ login, password, fileID }) {
  return soapEnvelope("DownloadTransactionFile", {
    login,
    pwd: password,
    fileID,
  });
}

export function buildSetTransactionDownloadedEnvelope({ login, password, fileID }) {
  return soapEnvelope("SetTransactionDownloaded", {
    login,
    pwd: password,
    fileID,
  });
}

export function buildSearchTransactionsEnvelope({
  login,
  password,
  direction,
  callerLicense,
  ePartner,
  transactionID,
  transactionStatus,
  transactionFileName,
  transactionFromDate,
  transactionToDate,
  minRecordCount,
  maxRecordCount,
}) {
  return soapEnvelope("SearchTransactions", {
    login,
    pwd: password,
    direction,
    callerLicense,
    ePartner,
    transactionID,
    transactionStatus,
    transactionFileName,
    transactionFromDate,
    transactionToDate,
    minRecordCount,
    maxRecordCount,
  });
}

export function parseDhpoFilesXml(xmlText) {
  const doc = new DOMParser().parseFromString(xmlText, "application/xml");
  const parserError = doc.querySelector("parsererror");
  if (parserError) {
    throw new Error("DHPO returned invalid file-list XML.");
  }

  return Array.from(doc.querySelectorAll("File")).map((node) => ({
    fileID: node.getAttribute("FileID") ?? "",
    fileName: node.getAttribute("FileName") ?? "",
    senderID: node.getAttribute("SenderID") ?? "",
    receiverID: node.getAttribute("ReceiverID") ?? "",
    transactionDate: node.getAttribute("TransactionDate") ?? "",
    recordCount: Number(node.getAttribute("RecordCount") ?? 0),
    isDownloaded: (node.getAttribute("IsDownloaded") ?? "False").toLowerCase() === "true",
  }));
}

export function getSampleDhpoFiles() {
  return parseDhpoFilesXml(SAMPLE_DHPO_FILES_XML);
}

export async function callDhpoSoap({ endpoint = DHPO_ENDPOINTS.live, methodName, envelope, signal }) {
  const response = await fetch(endpoint, {
    method: "POST",
    headers: {
      "Content-Type": "text/xml; charset=utf-8",
      SOAPAction: `${SOAP_NS}${methodName}`,
    },
    body: envelope,
    signal,
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(`DHPO ${methodName} failed with HTTP ${response.status}.`);
  }
  return text;
}
