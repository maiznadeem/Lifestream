// Cloudflare Worker on api.lifestream.himaiz.com: the zone's WAF runs before it, then it forwards every request
// unchanged to the Container Apps API, adding the shared secret the API requires and the visitor's address.
// Bodies stream through. The desktop app retries a 502 while a cold start is under way, so a failed fetch
// (the origin unreachable for a moment) is answered with a 502 rather than Cloudflare's error page.
//
// Logging (OracleVMOps LOGGING.md): one JSON line per request with part 'edge', and the visitor's ray forwarded as
// X-CF-Ray, which the API logs as `ray` (Cloudflare gives this Worker's own call to the origin a new CF-Ray).
export default {
    async fetch(request, env) {
        const started = Date.now();
        const url = new URL(request.url);
        const ray = request.headers.get('cf-ray') ?? '';
        const headers = new Headers(request.headers);
        for (const name of ['host', 'x-origin-auth', 'x-client-ip', 'x-forwarded-host', 'x-forwarded-proto', 'x-cf-ray']) headers.delete(name);
        headers.set('x-origin-auth', env.ORIGIN_AUTH);
        headers.set('x-client-ip', request.headers.get('cf-connecting-ip') ?? '');
        headers.set('x-forwarded-host', url.host);
        headers.set('x-forwarded-proto', 'https');
        if (ray) headers.set('x-cf-ray', ray);

        // Copy the fixed origin and set only path and query. Resolving the path as a relative URL would let a
        // path like "//evil.example/x" send the request (and the secret) to another host.
        const target = new URL(env.ORIGIN);
        target.pathname = url.pathname;
        target.search = url.search;

        let response;
        try {
            response = await fetch(target, {
                method: request.method,
                headers,
                body: request.body,
                redirect: 'manual',
            });
        } catch {
            response = new Response('The server is not answering yet.', { status: 502, headers: { 'cache-control': 'no-store' } });
        }

        log(request.method, url.pathname, response.status, ray, Date.now() - started);
        return response;
    },
};

// The API's routes are words and numeric ids: ids become :id and anything else that is not a plain word becomes
// :param, so the line never carries a raw path segment. The query string (search terms) is never logged.
function routeOf(pathname) {
    const route = pathname
        .split('/')
        .map((part) => (part === '' || /^[a-z][a-z0-9-]{0,40}$/.test(part) ? part : /^\d+$/.test(part) ? ':id' : ':param'))
        .join('/');
    return route.length > 120 ? route.slice(0, 120) : route;
}

function levelOf(route, status) {
    if (route === '/healthz' || route === '/readyz') return 'debug';
    return status >= 500 ? 'error' : 'info';
}

function log(method, pathname, status, ray, ms) {
    const route = routeOf(pathname);
    const line = {
        time: new Date().toISOString(),
        level: levelOf(route, status),
        app: 'lifestream',
        part: 'edge',
        event: 'request',
        ...(ray ? { ray } : {}),
        msg: `${method} ${route} ${status}`,
        method,
        route,
        status,
        ms,
    };
    console.log(JSON.stringify(line));
}
