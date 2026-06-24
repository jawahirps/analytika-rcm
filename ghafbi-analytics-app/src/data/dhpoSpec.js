export const DHPO_ENDPOINTS = {
  live: "https://dhpo.eclaimlink.ae/ValidateTransactions.asmx",
  archive: "https://dhpo.eclaimlink.ae/ClaimsAndAuthorizationsArchive.asmx",
};

export const DHPO_RESULT_CODES = {
  3: "Member has no approved trade drugs; Prescription transaction is not returned.",
  2: "No new prior authorization transactions are available for download.",
  1: "E-claim transaction validation succeeded with warnings.",
  0: "Operation is successful.",
  "-1": "Login failed for the user.",
  "-2": "E-claim transaction validation failed with errors.",
  "-3": "Required input parameter is empty, null, or invalid.",
  "-4": "Unexpected error occurred.",
  "-5": "Date range is longer than the allowed period.",
  "-6": "The specified file is not found.",
  "-7": "Transaction is not supported.",
  "-10": "No search criteria found.",
};

export const DHPO_TRANSACTION_TYPES = [
  { value: "-1", label: "All transactions" },
  { value: "2", label: "Claim.Submission" },
  { value: "4", label: "Person.Register" },
  { value: "8", label: "Remittance.Advice" },
  { value: "16", label: "Prior.Request" },
  { value: "32", label: "Prior.Authorization" },
];

export const SAMPLE_DHPO_FILES_XML = `<Files>
  <File FileID="31830" FileName="ClaimSubmissionMay2026.xml" SenderID="MF000" ReceiverID="A002" TransactionDate="11/04/2026 10:44:03" RecordCount="15" IsDownloaded="False" />
  <File FileID="31824" FileName="RemittanceAdviceMay2026.xml" SenderID="MF001" ReceiverID="A002" TransactionDate="12/04/2026 18:12:10" RecordCount="8123" IsDownloaded="False" />
  <File FileID="31821" FileName="PriorAuthorizationMay2026.xml" SenderID="MF002" ReceiverID="A002" TransactionDate="31/03/2026 16:59:31" RecordCount="6930" IsDownloaded="True" />
</Files>`;

export const DHPO_FETCH_STEPS = [
  "GetNewTransactions returns XML file metadata.",
  "DownloadTransactionFile downloads each selected FileID.",
  "SetTransactionDownloaded marks each successful download.",
];
