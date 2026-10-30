import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readdirSync, readFileSync, rmSync, statSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const extensionRoot = path.resolve(here, '..');
const repoRoot = path.resolve(extensionRoot, '..');
const project = path.join(repoRoot, 'src', 'TokenTracker.Server', 'TokenTracker.Server.csproj');
const target = path.join(extensionRoot, 'server');
const rid = (process.env.TOKENTRACKER_TARGET_RID ?? 'win-x64').trim();
const executableName = rid.startsWith('win-')
  ? 'TokenTracker.Server.exe'
  : 'TokenTracker.Server';

rmSync(target, { recursive: true, force: true });
execFileSync(
  'dotnet',
  ['publish', project, '-c', 'Release', '-r', rid, '-o', target],
  { stdio: 'inherit' },
);

const removePdb = (directory) => {
  for (const entry of readdirSync(directory)) {
    const full = path.join(directory, entry);
    if (statSync(full).isDirectory()) {
      removePdb(full);
    } else if (entry.endsWith('.pdb')) {
      unlinkSync(full);
    }
  }
};
removePdb(target);

const executable = path.join(target, executableName);
const stat = statSync(executable);
const assetHash = createHash('sha256')
  .update(readFileSync(path.join(target, 'wwwroot', 'index.html')))
  .digest('hex')
  .slice(0, 8);
const buildId = `${Math.round(stat.mtimeMs)}-${stat.size}-${assetHash}`;
writeFileSync(
  path.join(target, 'build.json'),
  JSON.stringify(
    {
      buildId,
      rid,
      serverVersion: '1.0.9',
      createdAt: new Date().toISOString(),
    },
    null,
    2,
  ),
  'utf8',
);

console.log('server synced to', target, 'build', buildId);
