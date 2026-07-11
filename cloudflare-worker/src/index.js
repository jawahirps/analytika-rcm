const HOP_BY_HOP_HEADERS = [
  "connection",
  "keep-alive",
  "proxy-authenticate",
  "proxy-authorization",
  "te",
  "trailers",
  "transfer-encoding",
  "upgrade"
];

function getOrigin(env) {
  const origin = env.ANALYTIKA_TUNNEL_ORIGIN;
  if (!origin) {
    throw new Error("Missing ANALYTIKA_TUNNEL_ORIGIN environment variable.");
  }

  return new URL(origin);
}

function buildTargetUrl(request, env) {
  const target = getOrigin(env);
  const incoming = new URL(request.url);
  target.pathname = incoming.pathname;
  target.search = incoming.search;
  return target;
}

function copyHeaders(headers, hostname) {
  const outbound = new Headers(headers);

  for (const name of HOP_BY_HOP_HEADERS) {
    outbound.delete(name);
  }

  outbound.set("host", hostname);
  outbound.set("x-forwarded-host", headers.get("host") ?? hostname);
  outbound.set("x-forwarded-proto", "https");

  const cfConnectingIp = headers.get("cf-connecting-ip");
  if (cfConnectingIp) {
    outbound.set("x-forwarded-for", cfConnectingIp);
  }

  return outbound;
}

function withSecurityHeaders(response) {
  const headers = new Headers(response.headers);
  headers.set("x-robot-tag", "noindex");
  headers.set("x-content-type-options", "nosniff");
  headers.set("referrer-policy", "strict-origin-when-cross-origin");

  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers
  });
}

export default {
  async fetch(request, env) {
    const target = buildTargetUrl(request, env);
    const headers = copyHeaders(request.headers, target.host);

    const init = {
      method: request.method,
      headers,
      body: request.body,
      redirect: "manual",
      cf: {
        cacheTtl: 0,
        cacheEverything: false
      }
    };

    const response = await fetch(target, init);
    return withSecurityHeaders(response);
  }
};
