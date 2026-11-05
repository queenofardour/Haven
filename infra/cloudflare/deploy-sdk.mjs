import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const root = resolve(directory, "../..");
const wrangler = resolve(directory, "node_modules/wrangler/bin/wrangler.js");
const accountArg = process.argv.find(x => x.startsWith("--account="))?.slice(10);

function run(file, args, options = {}) {
  const result = spawnSync(file, args, { cwd: directory, encoding: "utf8", maxBuffer: 8 * 1024 * 1024, ...options });
  if (result.error) throw new Error(`Could not start ${file}: ${result.error.code}`);
  if (result.status !== 0) {
    // Credentials stay in memory, never in command arguments or printed child output.
    const hidden = options.env?.SDK_CONTAINER_REGISTRY_PWORD;
    let output = (result.stdout ?? "") + (result.stderr ?? "");
    if (hidden) output = output.replaceAll(hidden, "[redacted]");
    process.stderr.write(output);
    throw new Error(`${file} exited with status ${result.status}`);
  }
  return result.stdout;
}

try {
  const identity = JSON.parse(run(process.execPath, [wrangler, "whoami", "--json"]));
  const accounts = identity.accounts ?? [];
  const accountId = accountArg ?? (accounts.length === 1 ? accounts[0].id : undefined);
  if (!accountId || !/^[0-9a-f]{32}$/i.test(accountId))
    throw new Error("Select an account with npm run deploy:sdk -- --account=ACCOUNT_ID. Use wrangler whoami to list accounts.");
  if (!accounts.some(x => x.id === accountId)) throw new Error("The selected account is not available to this Cloudflare login.");
  const environment = { ...process.env, CLOUDFLARE_ACCOUNT_ID: accountId };
  const credentials = JSON.parse(run(process.execPath, [wrangler, "containers", "registries", "credentials", "registry.cloudflare.com", "--push", "--pull", "--expiration-minutes", "15", "--json"], { env: environment }));
  if (!credentials.username || !credentials.password) throw new Error("Cloudflare did not return registry credentials.");
  const version = readFileSync(resolve(root, "src/Haven/Haven.csproj"), "utf8").match(/<Version>([^<]+)<\/Version>/)?.[1];
  if (!version || !/^\d+\.\d+\.\d+$/.test(version)) throw new Error("Invalid plugin version.");
  const dotnet = resolve(root, ".tools/dotnet/dotnet.exe");
  // A unique immutable deployment tag also rolls out server-only fixes when the
  // plugin version stays unchanged. Reusing a mutable tag may not trigger a rollout.
  const imageTag = `${version}-${Date.now().toString(36)}`;
  const image = `registry.cloudflare.com/${accountId}/haven-relay:${imageTag}`;
  console.log(`Building and publishing Haven ${version} for linux/amd64. No Docker engine required.`);
  const build = run(existsSync(dotnet) ? dotnet : "dotnet", ["publish", resolve(root, "src/Haven.Relay/Haven.Relay.csproj"), "-c", "Release", "--os", "linux", "--arch", "x64", "/t:PublishContainer", "--nologo", "-p:HavenContainerBuild=true", "-p:ContainerRegistry=registry.cloudflare.com", `-p:ContainerRepository=${accountId}/haven-relay`, `-p:ContainerImageTag=${imageTag}`, "-p:ContainerBaseImage=mcr.microsoft.com/dotnet/aspnet:10.0"], {
    cwd: root,
    env: { ...environment, SDK_CONTAINER_REGISTRY_UNAME: credentials.username, SDK_CONTAINER_REGISTRY_PWORD: credentials.password }
  });
  console.log(build.replaceAll(credentials.password, "[redacted]"));
  const config = JSON.parse(readFileSync(resolve(directory, "wrangler.jsonc"), "utf8"));
  config.account_id = accountId;
  config.containers[0].image = image;
  delete config.containers[0].image_build_context;
  const generated = resolve(directory, "wrangler.deploy.json");
  writeFileSync(generated, JSON.stringify(config, null, 2) + "\n");
  console.log("Deploying the Worker and registered container image…");
  const deployment = spawnSync(process.execPath, [wrangler, "deploy", "--config", generated], { cwd: directory, env: environment, stdio: "inherit" });
  if (deployment.error || deployment.status !== 0) throw new Error("Cloudflare deployment did not complete. The uploaded image can be reused on retry.");
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
