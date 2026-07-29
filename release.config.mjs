export default {
  branches: ['main'],
  tagFormat: 'v${version}',
  plugins: [
    '@semantic-release/commit-analyzer',
    '@semantic-release/release-notes-generator',
    [
      '@semantic-release/github',
      {
        assets: [
          { path: 'artifacts/release/token-dashboard-win-x64.zip', label: 'Windows x64 archive' },
          { path: 'artifacts/release/token-dashboard-linux-x64.tar.gz', label: 'Linux x64 archive' },
          { path: 'artifacts/release/token-dashboard-osx-x64.tar.gz', label: 'macOS x64 archive' },
          { path: 'artifacts/release/install.ps1', label: 'PowerShell installer' },
          { path: 'artifacts/release/install.sh', label: 'POSIX shell installer' },
          { path: 'artifacts/release/SHA256SUMS', label: 'SHA-256 checksums' }
        ]
      }
    ]
  ]
};
