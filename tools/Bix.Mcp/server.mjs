import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.dirname(fileURLToPath(import.meta.url));
const knowledgePath = path.join(root, "knowledge", "denial-codes.json");
const markdownPath = path.resolve(root, "..", "..", "docs", "reference", "dhpo-denial-codes.md");
const knowledge = JSON.parse(fs.readFileSync(knowledgePath, "utf8"));
const byCode = new Map(knowledge.records.map(row => [row.code.toUpperCase(), row]));

function result(id, value) {
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, result: value }) + "\n");
}

function error(id, code, message) {
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, error: { code, message } }) + "\n");
}

function textContent(value) {
  return { content: [{ type: "text", text: JSON.stringify(value, null, 2) }] };
}

function handle(message) {
  const { id, method, params = {} } = message;
  if (method === "initialize") return result(id, {
    protocolVersion: params.protocolVersion || "2024-11-05",
    capabilities: { tools: {}, resources: {} },
    serverInfo: { name: "bix-knowledge", version: "1.0.0" }
  });
  if (method === "notifications/initialized" || method === "notifications/cancelled") return;
  if (method === "tools/list") return result(id, { tools: [
    {
      name: "lookup_denial_code",
      description: "Return the authoritative DHPO description and effective dates for one denial code.",
      inputSchema: { type: "object", properties: { code: { type: "string" } }, required: ["code"] }
    },
    {
      name: "search_denial_codes",
      description: "Search DHPO denial codes by code or description.",
      inputSchema: { type: "object", properties: { query: { type: "string" }, limit: { type: "integer", minimum: 1, maximum: 50, default: 10 } }, required: ["query"] }
    }
  ]});
  if (method === "tools/call") {
    const args = params.arguments || {};
    if (params.name === "lookup_denial_code") {
      const row = byCode.get(String(args.code || "").trim().toUpperCase());
      return result(id, textContent(row || { code: args.code, found: false }));
    }
    if (params.name === "search_denial_codes") {
      const query = String(args.query || "").trim().toLowerCase();
      const limit = Math.min(50, Math.max(1, Number(args.limit) || 10));
      const rows = knowledge.records.filter(row =>
        row.code.toLowerCase().includes(query) || row.description.toLowerCase().includes(query)
      ).slice(0, limit);
      return result(id, textContent({ query, count: rows.length, records: rows }));
    }
    return error(id, -32601, "Unknown Bix tool");
  }
  if (method === "resources/list") return result(id, { resources: [
    { uri: "bix://knowledge/denial-codes", name: "DHPO Denial Codes", mimeType: "text/markdown", description: "DHPO denial-code reference imported from the supplied workbook." },
    { uri: "bix://knowledge/denial-codes.json", name: "DHPO Denial Codes JSON", mimeType: "application/json" }
  ]});
  if (method === "resources/read") {
    if (params.uri === "bix://knowledge/denial-codes")
      return result(id, { contents: [{ uri: params.uri, mimeType: "text/markdown", text: fs.readFileSync(markdownPath, "utf8") }] });
    if (params.uri === "bix://knowledge/denial-codes.json")
      return result(id, { contents: [{ uri: params.uri, mimeType: "application/json", text: JSON.stringify(knowledge, null, 2) }] });
    return error(id, -32002, "Unknown Bix resource");
  }
  if (id !== undefined) error(id, -32601, "Method not found");
}

let buffer = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", chunk => {
  buffer += chunk;
  for (;;) {
    const newline = buffer.indexOf("\n");
    if (newline < 0) break;
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;
    try { handle(JSON.parse(line)); }
    catch (ex) { error(null, -32700, ex instanceof Error ? ex.message : String(ex)); }
  }
});
