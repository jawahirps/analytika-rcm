// Bix remote agent — a Claude Code session reachable over HTTP.
//
// Runs the Claude Agent SDK against the Bix repository and streams the conversation to a
// browser, so the same kind of work normally done at the desktop can be driven from a
// phone. Published at https://dev-bix.ghafservices.com/agent through the existing
// Cloudflare tunnel.
//
// This process can read and modify the source tree, so it is defended in three layers:
//   1. Cloudflare Access sits in front of the hostname.
//   2. Every request must carry the shared token from agent-token.txt.
//   3. Tool use goes through an allowlist (below) rather than blanket permission bypass —
//      an exposed shell is the thing that would turn a stolen session into a compromised
//      machine, so Bash is limited to named, non-destructive commands.
//
// Binds to loopback only; the tunnel is the sole way in.

import { createServer } from 'node:http';
import { randomBytes, timingSafeEqual } from 'node:crypto';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { query } from '@anthropic-ai/claude-agent-sdk';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = process.env.BIX_AGENT_REPO ?? 'J:\\Ghaf BI\\analytika-rcm';
const PORT = Number(process.env.BIX_AGENT_PORT ?? 5178);
const BASE = '/agent';                       // path the tunnel routes here
const TOKEN_FILE = join(HERE, 'agent-token.txt');

// ── token ────────────────────────────────────────────────────────────────────
// Generated once and kept on disk so restarts do not invalidate an open phone tab.
let TOKEN = process.env.BIX_AGENT_TOKEN ?? '';
if (!TOKEN) {
    if (existsSync(TOKEN_FILE)) TOKEN = readFileSync(TOKEN_FILE, 'utf8').trim();
    if (!TOKEN) {
        TOKEN = randomBytes(24).toString('base64url');
        writeFileSync(TOKEN_FILE, TOKEN + '\n', 'utf8');
    }
}

function tokenOk(supplied) {
    if (typeof supplied !== 'string' || supplied.length !== TOKEN.length) return false;
    return timingSafeEqual(Buffer.from(supplied), Buffer.from(TOKEN));
}

// ── tool policy ──────────────────────────────────────────────────────────────
// Reading and editing the repo is the point of this service. Running arbitrary shell
// commands is not: it is the difference between "can change code" and "owns the box".
const ALWAYS_ALLOW = new Set([
    'Read', 'Grep', 'Glob', 'Edit', 'Write', 'NotebookEdit',
    'TodoWrite', 'Task', 'WebFetch', 'WebSearch', 'Skill',
]);

// Bash is allowed only for these shapes — build, inspect, and probe the local app.
const BASH_ALLOW = [
    /^git\s+(status|diff|log|show|branch|remote|rev-parse)\b/i,
    /^dotnet\s+(build|publish|restore|--version)\b/i,
    /^curl\s+(-[a-zA-Z]+\s+)*https?:\/\/localhost(:\d+)?\//i,
    /^npm\s+(run|ls|--version)\b/i,
    /^(ls|dir|type|cat|head|tail|wc|find)\b/i,
];

function judgeBash(command) {
    const cmd = String(command ?? '').trim();
    // A chained command can smuggle anything past a prefix match.
    if (/[;&|>`]|\$\(/.test(cmd)) {
        return { behavior: 'deny', message: 'Chained or redirected shell commands are not allowed on the remote agent. Run one simple command.' };
    }
    if (BASH_ALLOW.some(re => re.test(cmd))) return { behavior: 'allow' };
    return {
        behavior: 'deny',
        message: `The remote agent may not run '${cmd.split(/\s+/)[0]}'. Allowed: git status/diff/log, dotnet build/publish, curl to localhost, npm run, and file listing. `
               + 'Ask the operator to run it at the desktop if it is genuinely needed.',
    };
}

const canUseTool = async (toolName, input) => {
    if (ALWAYS_ALLOW.has(toolName)) return { behavior: 'allow' };
    if (toolName === 'Bash') return judgeBash(input?.command);
    return { behavior: 'deny', message: `Tool '${toolName}' is not enabled on the remote agent.` };
};

const SYSTEM_APPEND = `
You are reached over the web from a phone, so keep replies short and concrete.
You are working in the Bix repository (ASP.NET Core MVC, healthcare RCM, UAE clinics).
The dev build runs at http://localhost:5001 and production at http://localhost:5000 —
never touch production data. You may edit source and run builds; you cannot restart
services or run arbitrary shell commands, so when something needs that, say so plainly
and leave it for the desktop session.
`.trim();

// ── conversation state ───────────────────────────────────────────────────────
// One session id per browser conversation; the SDK holds the transcript.
let sessionId = null;

async function runTurn(prompt, res) {
    const send = (event, data) => {
        res.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
    };

    try {
        const response = query({
            prompt,
            options: {
                cwd: REPO,
                canUseTool,
                permissionMode: 'default',
                systemPrompt: { type: 'preset', preset: 'claude_code', append: SYSTEM_APPEND },
                maxTurns: 40,
                ...(sessionId ? { resume: sessionId } : {}),
            },
        });

        for await (const message of response) {
            if (message.session_id) sessionId = message.session_id;

            if (message.type === 'assistant') {
                for (const block of message.message?.content ?? []) {
                    if (block.type === 'text' && block.text.trim()) send('text', { text: block.text });
                    else if (block.type === 'tool_use') send('tool', { name: block.name, input: block.input });
                }
            } else if (message.type === 'system' && message.subtype === 'permission_denied') {
                send('denied', { text: message.message ?? 'Tool denied.' });
            } else if (message.type === 'result') {
                send('done', {
                    text: message.subtype === 'success' ? message.result : `Ended: ${message.subtype}`,
                    isError: Boolean(message.is_error),
                    turns: message.num_turns,
                    costUsd: message.total_cost_usd,
                });
            }
        }
    } catch (err) {
        send('error', { text: String(err?.message ?? err) });
    } finally {
        res.end();
    }
}

// ── http ─────────────────────────────────────────────────────────────────────
function readBody(req) {
    return new Promise((resolve, reject) => {
        let data = '';
        req.on('data', c => {
            data += c;
            if (data.length > 200_000) { reject(new Error('body too large')); req.destroy(); }
        });
        req.on('end', () => resolve(data));
        req.on('error', reject);
    });
}

const server = createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost');
    const path = url.pathname.replace(/\/+$/, '') || BASE;

    if (path === BASE || path === `${BASE}/`) {
        const html = readFileSync(join(HERE, 'index.html'), 'utf8');
        res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
        return res.end(html);
    }

    if (path === `${BASE}/api/health`) {
        res.writeHead(200, { 'content-type': 'application/json' });
        return res.end(JSON.stringify({ ok: true, repo: REPO, session: sessionId }));
    }

    if (path === `${BASE}/api/chat` && req.method === 'POST') {
        let body;
        try { body = JSON.parse(await readBody(req)); }
        catch { res.writeHead(400); return res.end('bad request'); }

        if (!tokenOk(body.token)) {
            res.writeHead(401, { 'content-type': 'application/json' });
            return res.end(JSON.stringify({ error: 'Bad or missing access token.' }));
        }
        const prompt = String(body.message ?? '').trim();
        if (!prompt) { res.writeHead(400); return res.end('empty message'); }
        if (body.reset) sessionId = null;

        res.writeHead(200, {
            'content-type': 'text/event-stream',
            'cache-control': 'no-store',
            'connection': 'keep-alive',
            'x-accel-buffering': 'no',
        });
        return runTurn(prompt, res);
    }

    res.writeHead(404);
    res.end('not found');
});

server.listen(PORT, '127.0.0.1', () => {
    console.log(`bix-agent listening on http://127.0.0.1:${PORT}${BASE}`);
    console.log(`repo: ${REPO}`);
    console.log(`token file: ${TOKEN_FILE}`);
});
