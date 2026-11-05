interface DownloadEnv {
  HAVEN_DOWNLOADS: KVNamespace;
  HAVEN_DOWNLOAD_KEY: string;
  HAVEN_PUBLISH_KEY: string;
}
const versionPattern = /^\d+\.\d+\.\d+$/;
const maxAsset = 8 * 1024 * 1024;
const headers = { "X-Robots-Tag": "noindex, nofollow, noarchive", "Referrer-Policy": "no-referrer",
  "Cache-Control": "private, no-store", "X-Content-Type-Options": "nosniff" };
function reply(body: BodyInit | null, status = 200, extra: Record<string, string> = {}): Response {
  return new Response(body, { status, headers: { ...headers, ...extra } });
}
async function equalSecret(actual: string, expected: string): Promise<boolean> {
  if (!expected || expected.length < 32) return false;
  const digest = async (s: string) => new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s)));
  const [a, b] = await Promise.all([digest(actual), digest(expected)]);
  let difference = 0; for (let i = 0; i < a.length; i++) difference |= a[i] ^ b[i];
  return difference === 0;
}
const assetKey = (version: string, name: string) => `releases/v${version}/${name}`;

export default {
  async fetch(request: Request, env: DownloadEnv): Promise<Response> {
    const url = new URL(request.url);
    const parts = url.pathname.split("/").filter(Boolean);
    if (parts[0] === "_publish") {
      if (!await equalSecret(request.headers.get("Authorization") ?? "", `Bearer ${env.HAVEN_PUBLISH_KEY ?? ""}`)
        || !env.HAVEN_PUBLISH_KEY || env.HAVEN_PUBLISH_KEY.length < 32) return reply("Not found", 404);
      if (request.method === "PUT" && parts.length === 3 && /^v\d+\.\d+\.\d+$/.test(parts[1])) {
        const version = parts[1].slice(1), name = parts[2];
        if (![ `Haven-alpha-${version}.zip`, `Haven-source-${version}.zip` ].includes(name)) return reply("Invalid asset", 400);
        if (Number(request.headers.get("Content-Length")) > maxAsset) return reply("Too large", 413);
        const data = await request.arrayBuffer();
        if (data.byteLength > maxAsset || data.byteLength < 22 || new Uint8Array(data)[0] !== 0x50 || new Uint8Array(data)[1] !== 0x4B)
          return reply("Invalid ZIP", 400);
        const hash = [...new Uint8Array(await crypto.subtle.digest("SHA-256", data))].map(x => x.toString(16).padStart(2, "0")).join("");
        const key = assetKey(version, name);
        const old = await env.HAVEN_DOWNLOADS.getWithMetadata<{ hash: string }>(key, "stream");
        if (old.value) { await old.value.cancel(); return reply(old.metadata?.hash === hash ? "Already uploaded" : "Version already exists", old.metadata?.hash === hash ? 200 : 409); }
        await env.HAVEN_DOWNLOADS.put(key, data, { metadata: { hash } });
        return reply("Uploaded", 201);
      }
      if (request.method === "POST" && url.pathname === "/_publish/promote") {
        const raw = await request.text(); if (raw.length > 32768) return reply("Too large", 413);
        let entry: any; try { entry = JSON.parse(raw); } catch { return reply("Invalid manifest", 400); }
        const version = String(entry.AssemblyVersion ?? "").replace(/\.0$/, "");
        if (entry.InternalName !== "Haven" || !versionPattern.test(version) || entry.AssemblyVersion !== `${version}.0`
          || !Number.isInteger(entry.DalamudApiLevel) || entry.DalamudApiLevel < 1) return reply("Invalid manifest", 400);
        for (const kind of ["alpha", "source"]) {
          const asset = await env.HAVEN_DOWNLOADS.get(assetKey(version, `Haven-${kind}-${version}.zip`), "stream");
          if (!asset) return reply("Assets not ready", 409);
          await asset.cancel();
        }
        await env.HAVEN_DOWNLOADS.put("latest", JSON.stringify({ version, entry, publishedAt: Math.floor(Date.now() / 1000) }));
        return reply("Published");
      }
      return reply("Not found", 404);
    }
    if (parts.length === 0 || !await equalSecret(parts[0], env.HAVEN_DOWNLOAD_KEY)) return reply("Not found", 404);
    if (request.method !== "GET" && request.method !== "HEAD") return reply("Method not allowed", 405);
    const latest = await env.HAVEN_DOWNLOADS.get<{ version: string; entry: any; publishedAt: number }>("latest", "json");
    if (!latest) return reply("Not found", 404);
    const base = `${url.origin}/${parts[0]}`;
    const download = `${base}/releases/v${latest.version}/Haven-alpha-${latest.version}.zip`;
    const source = `${base}/releases/v${latest.version}/Haven-source-${latest.version}.zip`;
    if (parts.length === 2 && parts[1] === "pluginmaster.json") {
      const entry = { ...latest.entry, RepoUrl: `${base}/`, DownloadLinkInstall: download, DownloadLinkUpdate: download,
        IsHide: false, IsTestingExclusive: false, LastUpdate: latest.publishedAt };
      delete entry.DownloadLinkTesting;
      return reply(request.method === "HEAD" ? null : JSON.stringify([entry]), 200, { "Content-Type": "application/json; charset=utf-8" });
    }
    if (parts.length === 1) return reply(request.method === "HEAD" ? null : `<!doctype html><title>Haven private alpha</title><h1>Haven ${latest.version}</h1><p>Private alpha for invited testers. Please keep this link within the test group.</p><p><a href="${base}/pluginmaster.json">Dalamud repository</a> · <a href="${download}">Plugin ZIP</a> · <a href="${source}">Complete corresponding source (AGPL-3.0-or-later)</a></p>`, 200, { "Content-Type": "text/html; charset=utf-8" });
    if (parts.length === 4 && parts[1] === "releases" && /^v\d+\.\d+\.\d+$/.test(parts[2])) {
      const version = parts[2].slice(1), name = parts[3];
      if (![ `Haven-alpha-${version}.zip`, `Haven-source-${version}.zip` ].includes(name)) return reply("Not found", 404);
      const data = await env.HAVEN_DOWNLOADS.get(assetKey(version, name), "stream");
      if (!data) return reply("Not found", 404);
      if (request.method === "HEAD") await data.cancel();
      return reply(request.method === "HEAD" ? null : data, 200, { "Content-Type": "application/zip", "Content-Disposition": `attachment; filename="${name}"` });
    }
    return reply("Not found", 404);
  }
} satisfies ExportedHandler<DownloadEnv>;
