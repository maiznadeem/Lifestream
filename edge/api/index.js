// Cloudflare Worker on api.lifestream.himaiz.com: the zone's WAF runs before it, then it forwards every request
// unchanged to the Container Apps API, adding the shared secret the API requires and the visitor's address.
// Bodies stream through. The desktop app retries a 502 while a cold start is under way, so a failed fetch
// (the origin unreachable for a moment) is answered with a 502 rather than Cloudflare's error page.
export default {
    async fetch(request, env) {
        const url = new URL(request.url);
        const headers = new Headers(request.headers);
        for (const name of ['host', 'x-origin-auth', 'x-client-ip', 'x-forwarded-host', 'x-forwarded-proto']) headers.delete(name);
        headers.set('x-origin-auth', env.ORIGIN_AUTH);
        headers.set('x-client-ip', request.headers.get('cf-connecting-ip') ?? '');
        headers.set('x-forwarded-host', url.host);
        headers.set('x-forwarded-proto', 'https');

        // Copy the fixed origin and set only path and query. Resolving the path as a relative URL would let a
        // path like "//evil.example/x" send the request (and the secret) to another host.
        const target = new URL(env.ORIGIN);
        target.pathname = url.pathname;
        target.search = url.search;

        try {
            return await fetch(target, {
                method: request.method,
                headers,
                body: request.body,
                redirect: 'manual',
            });
        } catch {
            return new Response('The server is not answering yet.', { status: 502, headers: { 'cache-control': 'no-store' } });
        }
    },
};
