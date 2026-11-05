import test from "node:test";
import assert from "node:assert/strict";
import worker from "../src/downloads.ts";

class Store {
  data = new Map();
  async put(key, value, options = {}) { this.data.set(key, { value: typeof value === 'string' ? new TextEncoder().encode(value).buffer : value, metadata: options.metadata }); }
  async get(key, type) {
    const item = this.data.get(key); if (!item) return null;
    if (type === "json") return JSON.parse(new TextDecoder().decode(item.value));
    if (type === "stream") return new Response(item.value).body;
    return new TextDecoder().decode(item.value);
  }
  async getWithMetadata(key, type) { return { value: await this.get(key, type), metadata: this.data.get(key)?.metadata }; }
}
const env = () => ({ HAVEN_DOWNLOADS: new Store(), HAVEN_DOWNLOAD_KEY: 'r'.repeat(64), HAVEN_PUBLISH_KEY: 'p'.repeat(64) });
const fetch = (environment, path, method = 'GET', body, authorized = false) => worker.fetch(new Request('https://download.example'+path,
  { method, body, headers: authorized ? { Authorization: 'Bearer '+environment.HAVEN_PUBLISH_KEY } : {} }), environment);
const manifest = { InternalName: 'Haven', AssemblyVersion: '0.9.0.0', DalamudApiLevel: 15, DownloadLinkInstall: 'https://old.example/', RepoUrl: 'https://github.com/private' };
const zip = new Uint8Array([0x50,0x4B,...new Array(24).fill(0)]);

test('unlisted downloads fail closed without the link; knowing the link cannot publish', async () => {
  const e=env();
  for (const path of ['/', '/pluginmaster.json', '/robots.txt', '/wrong/pluginmaster.json', '/_publish/promote']) {
    const response=await fetch(e,path); assert.equal(response.status,404); assert.match(response.headers.get('X-Robots-Tag'),/noindex/);
  }
  assert.equal((await fetch(e,'/_publish/promote','POST',JSON.stringify(manifest))).status,404);
});

test('feed requires both packages and rewrites every download link behind the invitation', async () => {
  const e=env();
  assert.equal((await fetch(e,'/_publish/promote','POST',JSON.stringify(manifest),true)).status,409);
  for(const kind of ['alpha','source']) assert.equal((await fetch(e,`/_publish/v0.9.0/Haven-${kind}-0.9.0.zip`,'PUT',zip,true)).status,201);
  assert.equal((await fetch(e,'/_publish/promote','POST',JSON.stringify(manifest),true)).status,200);
  const response=await fetch(e,`/${e.HAVEN_DOWNLOAD_KEY}/pluginmaster.json`);
  const [entry]=await response.json(); assert.equal(entry.AssemblyVersion,'0.9.0.0');
  assert.ok(entry.DownloadLinkInstall.startsWith(`https://download.example/${e.HAVEN_DOWNLOAD_KEY}/releases/`));
  assert.equal(entry.DownloadLinkInstall,entry.DownloadLinkUpdate);
  assert.equal((await fetch(e,new URL(entry.DownloadLinkInstall).pathname)).status,200);
  assert.equal((await fetch(e,`/${e.HAVEN_DOWNLOAD_KEY}/releases/v0.9.0/Haven-source-0.9.0.zip`)).status,200);
  assert.match(await (await fetch(e,`/${e.HAVEN_DOWNLOAD_KEY}/`)).text(),/corresponding source/);
  assert.equal((await fetch(e,'/releases/v0.9.0/Haven-alpha-0.9.0.zip')).status,404);
});

test('published version assets are immutable; rotating invitation invalidates old links', async () => {
  const e=env(), path='/_publish/v0.9.0/Haven-alpha-0.9.0.zip';
  assert.equal((await fetch(e,path,'PUT',zip,true)).status,201);
  assert.equal((await fetch(e,path,'PUT',zip,true)).status,200);
  assert.equal((await fetch(e,path,'PUT',new Uint8Array([...zip,1]),true)).status,409);
  const old=e.HAVEN_DOWNLOAD_KEY; e.HAVEN_DOWNLOAD_KEY='s'.repeat(64);
  assert.equal((await fetch(e,`/${old}/releases/v0.9.0/Haven-alpha-0.9.0.zip`)).status,404);
  assert.equal((await fetch(e,'/_publish/v0.9.0/not-a-package.zip','PUT',zip,true)).status,400);
});
