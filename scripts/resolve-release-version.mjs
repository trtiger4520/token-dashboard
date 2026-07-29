import { Writable } from 'node:stream';
import semanticRelease from 'semantic-release';

const log = new Writable({
  write(chunk, _encoding, callback) {
    process.stderr.write(chunk);
    callback();
  }
});

const result = await semanticRelease(
  {
    ci: true,
    dryRun: true
  },
  {
    cwd: process.cwd(),
    env: process.env,
    stdout: log,
    stderr: log
  }
);

if (!result) {
  console.log('hasRelease=false');
  process.exit(0);
}

console.log('hasRelease=true');
console.log(`version=${result.nextRelease.version}`);
console.log(`tag=${result.nextRelease.gitTag}`);
